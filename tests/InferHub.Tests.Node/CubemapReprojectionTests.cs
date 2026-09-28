using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// The real <c>diffusion_worker.py</c> cuts a panorama into a cubemap the way phase-88 D2 says:
/// six square faces, OpenGL order, OpenGL orientation, the join on −X's centre line.
/// </summary>
/// <remarks>
/// <para>
/// <b>These run the shipped worker's own functions</b>, imported from <c>python/tools/</c> with
/// <c>importlib</c> — the same trick 57's image check used inside the container — under an interpreter
/// that has numpy and Pillow (<see cref="PythonNumpyFactAttribute"/>). Nothing here reimplements the
/// arithmetic in C#: a C# copy agreeing with itself would prove nothing about the bytes a client gets.
/// </para>
/// <para>
/// <b>The panorama is direction-coded.</b> Every pixel's colour is its own view direction,
/// <c>(d + 1) / 2 · 255</c>, so a cube pixel's colour decodes straight back to the direction it was
/// sampled from. That turns "is the orientation right" from a matter of looking at a picture into
/// a comparison with the GL table, which is the only statement of the convention that is not a
/// viewer's behaviour.
/// </para>
/// </remarks>
public class CubemapReprojectionTests
{
    private const string Prelude = """
        import importlib.util, sys
        import numpy
        from PIL import Image

        spec = importlib.util.spec_from_file_location("diffusion_worker", sys.argv[1])
        dw = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(dw)

        def coded(width, height):
            cols = (numpy.arange(width) + 0.5) / width
            rows = (numpy.arange(height) + 0.5) / height
            lon, lat = numpy.meshgrid((cols - 0.5) * 2 * numpy.pi, (0.5 - rows) * numpy.pi)
            d = numpy.stack([numpy.cos(lat) * numpy.cos(lon), numpy.sin(lat), numpy.cos(lat) * numpy.sin(lon)], -1)
            return Image.fromarray(numpy.rint((d + 1) / 2 * 255).astype(numpy.uint8), "RGB")

        def faces(cube):
            a = numpy.asarray(cube, dtype=numpy.float64) / 255 * 2 - 1
            edge = a.shape[0]
            return {name: a[:, i * edge:(i + 1) * edge] for i, name in enumerate(dw.CUBEMAP_FACES)}, edge

        def near(actual, expected, tolerance=0.03):
            expected = numpy.asarray(expected, dtype=numpy.float64)
            expected = expected / numpy.linalg.norm(expected)
            assert numpy.abs(actual - expected).max() < tolerance, f"{numpy.round(actual, 3)} != {numpy.round(expected, 3)}"

        """;

    [PythonNumpyFact]
    public void TheStripIsSixSquareFacesAQuarterOfTheWidthOnASide() => Run("""
        for width, height in ((2048, 1024), (1536, 768), (1024, 512)):
            cube = dw.equirect_to_cubemap(Image.new("RGB", (width, height)))
            assert cube is not None, (width, height)
            assert cube.size == (6 * (width // 4), width // 4), (width, height, cube.size)
        assert dw.CUBEMAP_FACES == ("px", "nx", "py", "ny", "pz", "nz")
        """);

    [PythonNumpyFact]
    public void EveryFaceCentreLooksDownItsOwnAxis() => Run("""
        f, edge = faces(dw.equirect_to_cubemap(coded(1024, 512)))
        axes = {"px": (1, 0, 0), "nx": (-1, 0, 0), "py": (0, 1, 0), "ny": (0, -1, 0), "pz": (0, 0, 1), "nz": (0, 0, -1)}
        for name, axis in axes.items():
            c = edge // 2
            near(f[name][c - 1:c + 1, c - 1:c + 1].mean(axis=(0, 1)), axis)
        """);

    /// <summary>
    /// GL 4.6 table 8.19, face by face: where the <b>top</b> edge (<c>tc = −1</c>) and the
    /// <b>right</b> edge (<c>sc = +1</c>) of each face point. A face that is right in the middle
    /// and mirrored or rotated passes the centre test and fails this one.
    /// </summary>
    [PythonNumpyFact]
    public void EveryFaceIsOrientedAsTheOpenGlTableSays() => Run("""
        f, edge = faces(dw.equirect_to_cubemap(coded(1024, 512)))
        c = edge // 2
        # name: (top-edge direction, right-edge direction), each the face axis plus one unit vector.
        expected = {
            "px": ((1, 1, 0), (1, 0, -1)),
            "nx": ((-1, 1, 0), (-1, 0, 1)),
            "py": ((0, 1, -1), (1, 1, 0)),
            "ny": ((0, -1, 1), (1, -1, 0)),
            "pz": ((0, 1, 1), (1, 0, 1)),
            "nz": ((0, 1, -1), (-1, 0, -1)),
        }
        for name, (top, right) in expected.items():
            near(f[name][0, c - 1:c + 1].mean(axis=0), top, 0.05)
            near(f[name][c - 1:c + 1, -1].mean(axis=0), right, 0.05)
        """);

    /// <summary>
    /// D4's claim, which is what makes <c>seam_delta</c> still mean something after the cut: the
    /// panorama's first and last columns — the join — land on −X's vertical centre line and
    /// nowhere else.
    /// </summary>
    [PythonNumpyFact]
    public void ThePanoramasJoinLandsOnTheCentreLineOfNegativeX() => Run("""
        width, height = 1024, 512
        a = numpy.zeros((height, width, 3), dtype=numpy.uint8)
        a[3 * height // 8:5 * height // 8, 0] = 255
        a[3 * height // 8:5 * height // 8, -1] = 255
        f, edge = faces(dw.equirect_to_cubemap(Image.fromarray(a, "RGB")))
        lit = {name: (face > -0.9).any(axis=2) for name, face in f.items()}
        for name in ("px", "py", "ny", "pz", "nz"):
            assert not lit[name].any(), f"the join reached {name}"
        columns = numpy.nonzero(lit["nx"].any(axis=0))[0]
        assert len(columns) > 0, "the join vanished"
        assert abs(columns.mean() - (edge - 1) / 2) < 1.0, columns
        assert columns.max() - columns.min() <= 2, columns
        """);

    [PythonNumpyFact]
    public void AFlatRecipeAndAnUnknownValueAreRefusedAndOffIsNothing() => Run("""
        panorama = {"id": "qwen-360", "projection": "equirectangular"}
        flat = {"id": "sdxl"}
        assert dw.resolve_reproject(panorama, {}) is None
        assert dw.resolve_reproject(panorama, {"reproject": "off"}) is None
        assert dw.resolve_reproject(panorama, {"reproject": " CubeMap "}) == "cubemap"
        for recipe, payload, words in ((flat, {"reproject": "cubemap"}, "cannot be cut into a cubemap"),
                                       (panorama, {"reproject": "cross"}, "is not one this worker knows")):
            try:
                dw.resolve_reproject(recipe, payload)
            except dw.ToolError as error:
                assert words in str(error), str(error)
                assert error.code == dw.ERROR_INVALID_REQUEST
            else:
                raise AssertionError(f"{payload} on {recipe['id']} was not refused")
        """);

    /// <summary>
    /// D5's precondition: something that is not a 2:1 panorama comes back as <c>None</c>, which the
    /// batch loop turns into "keep the panorama, say <c>reproject</c>" — never an exception on the
    /// last line of a two-minute job. And D3's request-level projection claims <c>cubemap</c> only
    /// when every image in the batch is one.
    /// </summary>
    [PythonNumpyFact]
    public void AnImageThatIsNotAPanoramaIsDeclinedRatherThanRaised() => Run("""
        assert dw.equirect_to_cubemap(Image.new("RGB", (512, 512))) is None
        assert dw.equirect_to_cubemap(Image.new("RGB", (2, 1))) is None
        assert dw.equirect_to_cubemap(object()) is None
        recipe = {"id": "qwen-360", "projection": "equirectangular"}
        assert dw.batch_projection(recipe, [{"projection": "cubemap"}, {"projection": "cubemap"}]) == "cubemap"
        assert dw.batch_projection(recipe, [{"projection": "cubemap"}, {"projection": "equirectangular"}]) == "equirectangular"
        assert dw.batch_projection({"id": "sdxl"}, []) == "flat"
        """);

    private static void Run(string body)
    {
        var python = PythonNumpyTestGate.Interpreter!;
        var worker = Path.Combine(RepositoryRoot(), "python", "tools", "diffusion_worker.py");
        var script = Path.Combine(Path.GetTempPath(), $"inferhub-cubemap-{Guid.NewGuid():N}.py");

        File.WriteAllText(script, Prelude + body + "\nprint(\"ok\")\n");

        try
        {
            var start = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            start.ArgumentList.Add(script);
            start.ArgumentList.Add(worker);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            Assert.True(process.WaitForExit(120_000), "the worker's functions did not finish in two minutes");

            Assert.True(
                process.ExitCode == 0 && stdout.Result.Contains("ok"),
                $"exit {process.ExitCode}\n{stdout.Result}\n{stderr.Result}");
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
