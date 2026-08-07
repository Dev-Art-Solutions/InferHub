// An equirectangular 360° viewer, in WebGL, by hand (phase 49, D5).
//
// Design rule 3 is build-free UI: no npm, no bundler, no framework. Adding three.js from a CDN
// would also put a third-party script on an admin console that holds cordon and model-pull rights,
// which is a worse trade than an afternoon of gl.texImage2D — and vendoring 600 KB of it into
// wwwroot is worse again.
//
// Usage:
//
//   const pano = InferHubPano.mount(canvasElement);
//   pano.load(blobOrObjectUrl);   // an equirectangular image, any 2:1 size
//   pano.setFlat(true);           // stop projecting; show the raw image instead
//   pano.destroy();
//
// Drag to look, wheel to zoom, arrow keys when the canvas has focus. Nothing here decodes a pixel
// on the server — this is the browser doing what browsers do, and the hub still never opens an
// image (phase-46 D6).

const InferHubPano = (() => {
  'use strict';

  const VERTEX = `
    attribute vec3 aPosition;
    attribute vec2 aUv;
    uniform mat4 uProjection;
    uniform mat4 uView;
    varying vec2 vUv;
    void main() {
      vUv = aUv;
      gl_Position = uProjection * uView * vec4(aPosition, 1.0);
    }`;

  const FRAGMENT = `
    precision mediump float;
    uniform sampler2D uTexture;
    varying vec2 vUv;
    void main() {
      gl_FragColor = texture2D(uTexture, vUv);
    }`;

  // ---- the small amount of linear algebra this needs ------------------------------------------

  function perspective(fovYRadians, aspect, near, far) {
    const f = 1 / Math.tan(fovYRadians / 2);
    const range = 1 / (near - far);

    return new Float32Array([
      f / aspect, 0, 0, 0,
      0, f, 0, 0,
      0, 0, (near + far) * range, -1,
      0, 0, near * far * range * 2, 0
    ]);
  }

  // The camera sits at the centre of the sphere and only ever rotates, so the view matrix is the
  // inverse of a yaw-then-pitch rotation — which for a pure rotation is its transpose, written out.
  function view(yaw, pitch) {
    const cy = Math.cos(yaw);
    const sy = Math.sin(yaw);
    const cp = Math.cos(pitch);
    const sp = Math.sin(pitch);

    return new Float32Array([
      cy, sy * sp, -sy * cp, 0,
      0, cp, sp, 0,
      sy, -cy * sp, cy * cp, 0,
      0, 0, 0, 1
    ]);
  }

  // ---- the sphere -------------------------------------------------------------------------------

  // Longitude across, latitude down: exactly what "equirectangular" means, so the UVs are the
  // image's own coordinates with no remapping. A viewer that had to remap would be a viewer with an
  // opinion about the projection, and the projection is the worker's to declare.
  function sphere(stacks, slices) {
    const positions = [];
    const uvs = [];
    const indices = [];

    for (let i = 0; i <= stacks; i++) {
      const v = i / stacks;
      const phi = v * Math.PI;

      for (let j = 0; j <= slices; j++) {
        const u = j / slices;
        const theta = u * Math.PI * 2;

        positions.push(
          Math.sin(phi) * Math.cos(theta),
          Math.cos(phi),
          Math.sin(phi) * Math.sin(theta));

        uvs.push(u, v);
      }
    }

    for (let i = 0; i < stacks; i++) {
      for (let j = 0; j < slices; j++) {
        const a = (i * (slices + 1)) + j;
        const b = a + slices + 1;

        indices.push(a, b, a + 1, b, b + 1, a + 1);
      }
    }

    return {
      positions: new Float32Array(positions),
      uvs: new Float32Array(uvs),
      indices: new Uint16Array(indices)
    };
  }

  function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);

    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      throw new Error(gl.getShaderInfoLog(shader) || 'shader compilation failed');
    }

    return shader;
  }

  function mount(canvas, options) {
    const settings = Object.assign({ fov: 75, minFov: 30, maxFov: 100 }, options || {});
    const gl = canvas.getContext('webgl', { antialias: true, alpha: false });

    // No WebGL is a real state — a locked-down browser, a remote session, a VM with no GPU — and
    // the honest answer is the flat image, not a black rectangle. Same instinct as phase-39 D6:
    // announce what you found rather than failing silently.
    if (!gl) {
      return flatOnly(canvas, 'this browser has no WebGL, so the panorama is shown flat');
    }

    const program = gl.createProgram();
    gl.attachShader(program, compile(gl, gl.VERTEX_SHADER, VERTEX));
    gl.attachShader(program, compile(gl, gl.FRAGMENT_SHADER, FRAGMENT));
    gl.linkProgram(program);

    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
      return flatOnly(canvas, gl.getProgramInfoLog(program) || 'the viewer could not start');
    }

    const mesh = sphere(48, 96);
    const positionBuffer = gl.createBuffer();
    const uvBuffer = gl.createBuffer();
    const indexBuffer = gl.createBuffer();

    gl.bindBuffer(gl.ARRAY_BUFFER, positionBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, mesh.positions, gl.STATIC_DRAW);
    gl.bindBuffer(gl.ARRAY_BUFFER, uvBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, mesh.uvs, gl.STATIC_DRAW);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, mesh.indices, gl.STATIC_DRAW);

    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);

    // CLAMP_TO_EDGE and no mipmaps: an equirectangular render is not required to be a power of two
    // (1536×768 is not), and WebGL 1 refuses REPEAT and mipmaps on one that is not. The UVs already
    // land exactly on [0,1], so the wrap is handled by geometry rather than by the sampler.
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

    // One grey pixel until an image lands, so a canvas that has not been given one looks
    // deliberately empty rather than broken.
    gl.texImage2D(
      gl.TEXTURE_2D, 0, gl.RGB, 1, 1, 0, gl.RGB, gl.UNSIGNED_BYTE,
      new Uint8Array([32, 34, 38]));

    const attributes = {
      position: gl.getAttribLocation(program, 'aPosition'),
      uv: gl.getAttribLocation(program, 'aUv')
    };

    const uniforms = {
      projection: gl.getUniformLocation(program, 'uProjection'),
      view: gl.getUniformLocation(program, 'uView'),
      texture: gl.getUniformLocation(program, 'uTexture')
    };

    const state = {
      yaw: 0,
      pitch: 0,
      fov: settings.fov,
      dragging: false,
      lastX: 0,
      lastY: 0,
      flat: false,
      running: true,
      image: null,
      frame: 0
    };

    const flatImage = document.createElement('img');
    flatImage.alt = '';
    flatImage.style.cssText = 'display:none;width:100%;height:100%;object-fit:contain;';
    canvas.parentNode.insertBefore(flatImage, canvas.nextSibling);

    function resize() {
      const ratio = window.devicePixelRatio || 1;
      const width = Math.max(1, Math.round(canvas.clientWidth * ratio));
      const height = Math.max(1, Math.round(canvas.clientHeight * ratio));

      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
      }
    }

    function draw() {
      if (!state.running) {
        return;
      }

      state.frame = requestAnimationFrame(draw);

      if (state.flat) {
        return;
      }

      resize();

      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.clearColor(0.13, 0.14, 0.16, 1);
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

      // The camera is INSIDE the sphere, so the triangles face away from it. Culling nothing is
      // cheaper to reason about than flipping the winding order, and this mesh is 9k triangles.
      gl.disable(gl.CULL_FACE);
      gl.useProgram(program);

      gl.bindBuffer(gl.ARRAY_BUFFER, positionBuffer);
      gl.enableVertexAttribArray(attributes.position);
      gl.vertexAttribPointer(attributes.position, 3, gl.FLOAT, false, 0, 0);

      gl.bindBuffer(gl.ARRAY_BUFFER, uvBuffer);
      gl.enableVertexAttribArray(attributes.uv);
      gl.vertexAttribPointer(attributes.uv, 2, gl.FLOAT, false, 0, 0);

      gl.uniformMatrix4fv(
        uniforms.projection,
        false,
        perspective(state.fov * Math.PI / 180, canvas.width / canvas.height, 0.1, 10));

      gl.uniformMatrix4fv(uniforms.view, false, view(state.yaw, state.pitch));

      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.uniform1i(uniforms.texture, 0);

      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
      gl.drawElements(gl.TRIANGLES, mesh.indices.length, gl.UNSIGNED_SHORT, 0);
    }

    function onPointerDown(event) {
      state.dragging = true;
      state.lastX = event.clientX;
      state.lastY = event.clientY;
      canvas.setPointerCapture(event.pointerId);
    }

    function onPointerMove(event) {
      if (!state.dragging) {
        return;
      }

      // Scaled by the field of view, so dragging feels the same zoomed in as zoomed out.
      const scale = (state.fov / 75) * 0.005;

      state.yaw -= (event.clientX - state.lastX) * scale;
      state.pitch = clampPitch(state.pitch - ((event.clientY - state.lastY) * scale));
      state.lastX = event.clientX;
      state.lastY = event.clientY;
    }

    function onPointerUp(event) {
      state.dragging = false;

      if (canvas.hasPointerCapture(event.pointerId)) {
        canvas.releasePointerCapture(event.pointerId);
      }
    }

    function onWheel(event) {
      event.preventDefault();
      state.fov = Math.min(settings.maxFov, Math.max(settings.minFov, state.fov + (event.deltaY * 0.05)));
    }

    function onKeyDown(event) {
      const step = 0.08;
      const moves = {
        ArrowLeft: () => { state.yaw += step; },
        ArrowRight: () => { state.yaw -= step; },
        ArrowUp: () => { state.pitch = clampPitch(state.pitch + step); },
        ArrowDown: () => { state.pitch = clampPitch(state.pitch - step); }
      };

      if (moves[event.key]) {
        event.preventDefault();
        moves[event.key]();
      }
    }

    // A viewer that lets you tip past the pole shows you an upside-down world and no way back.
    function clampPitch(value) {
      const limit = (Math.PI / 2) - 0.01;
      return Math.min(limit, Math.max(-limit, value));
    }

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('keydown', onKeyDown);
    canvas.tabIndex = 0;
    canvas.style.touchAction = 'none';
    canvas.style.cursor = 'grab';

    state.frame = requestAnimationFrame(draw);

    return {
      load(source) {
        return new Promise((resolve, reject) => {
          const image = new Image();
          image.crossOrigin = 'anonymous';

          image.onload = () => {
            state.image = image;
            flatImage.src = image.src;

            gl.bindTexture(gl.TEXTURE_2D, texture);
            gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB, gl.RGB, gl.UNSIGNED_BYTE, image);

            state.yaw = 0;
            state.pitch = 0;
            resolve({ width: image.naturalWidth, height: image.naturalHeight });
          };

          image.onerror = () => reject(new Error('the image could not be loaded'));
          image.src = source;
        });
      },

      // The flat toggle is not a fallback, it is a view: distortion away from the horizon is a
      // property of the projection, and being able to see the raw frame is how somebody checks
      // whether a wrong-looking picture is the model or the viewer.
      setFlat(flat) {
        state.flat = !!flat;
        canvas.style.display = flat ? 'none' : '';
        flatImage.style.display = flat ? '' : 'none';
      },

      reset() {
        state.yaw = 0;
        state.pitch = 0;
        state.fov = settings.fov;
      },

      destroy() {
        state.running = false;
        cancelAnimationFrame(state.frame);
        canvas.removeEventListener('pointerdown', onPointerDown);
        canvas.removeEventListener('pointermove', onPointerMove);
        canvas.removeEventListener('pointerup', onPointerUp);
        canvas.removeEventListener('pointercancel', onPointerUp);
        canvas.removeEventListener('wheel', onWheel);
        canvas.removeEventListener('keydown', onKeyDown);
        flatImage.remove();
      }
    };
  }

  /// A degraded viewer that is honest about being one.
  function flatOnly(canvas, reason) {
    const image = document.createElement('img');
    image.alt = '';
    image.style.cssText = 'width:100%;height:100%;object-fit:contain;';
    canvas.parentNode.insertBefore(image, canvas.nextSibling);
    canvas.style.display = 'none';

    return {
      reason,
      load(source) {
        image.src = source;
        return Promise.resolve(null);
      },
      setFlat() {},
      reset() {},
      destroy() { image.remove(); }
    };
  }

  return { mount };
})();

if (typeof window !== 'undefined') {
  window.InferHubPano = InferHubPano;
}
