using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// Phase 90. The real <c>piper_worker.py</c> fetches a voice only when it is named, only from the
/// pinned catalogue, and only into place once both files match their sha256 — and it declares the
/// voice when it lands, over the real protocol.
/// </summary>
/// <remarks>
/// <para>
/// <b>These run the shipped worker</b>, imported with <c>importlib</c> or spawned as a process, the
/// way <c>CubemapReprojectionTests</c> runs the diffusion worker. The fetch path is stdlib only
/// (<c>urllib</c>, <c>hashlib</c>), so <see cref="PythonWorkerFactAttribute"/> is the gate — Piper
/// itself is imported only when a voice is loaded, which none of these do.
/// </para>
/// <para>
/// A local <c>http.server</c> stands in for Hugging Face through <c>HF_ENDPOINT</c>, the variable
/// the worker honours for a mirror. The bytes it serves are made up; the hashes the catalogue pins
/// are computed from them, or deliberately from something else.
/// </para>
/// </remarks>
public class VoiceCatalogueTests
{
    private const string Prelude = """
        import contextlib, functools, hashlib, http.server, importlib.util, io, json, os, queue
        import subprocess, sys, tempfile, threading, time

        WORKER, SHIPPED = sys.argv[1], sys.argv[2]
        REV = "0123456789abcdef0123456789abcdef01234567"
        root = tempfile.mkdtemp()
        served = os.path.join(root, "served")
        voices_dir = os.path.join(root, "voices")
        catalogue = os.path.join(root, "catalogue")
        os.makedirs(served)
        os.makedirs(catalogue)

        class Handler(http.server.SimpleHTTPRequestHandler):
            delay = 0.0
            def log_message(self, *args):
                pass
            def do_GET(self):
                time.sleep(Handler.delay)
                super().do_GET()

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=served))
        threading.Thread(target=server.serve_forever, daemon=True).start()

        os.environ.update({
            "HF_ENDPOINT": f"http://127.0.0.1:{server.server_port}",
            "INFERHUB_PIPER_VOICES": voices_dir,
            "INFERHUB_PIPER_CATALOGUE": catalogue,
            "INFERHUB_ALLOW_MODEL_DOWNLOAD": "1",
        })

        spec = importlib.util.spec_from_file_location("piper_worker", WORKER)
        pw = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(pw)

        def publish(voice, onnx=b"not really onnx", config=b"{}", pinned=None):
            # Serve the pair at the path Hugging Face would, and catalogue it with hashes of `pinned`
            # (the served bytes when absent).
            files = []
            for name, data in ((voice + ".onnx", onnx), (voice + ".onnx.json", config)):
                path = f"xx/{voice}/{name}"
                target = os.path.join(served, "test", "voices", "resolve", REV, *path.split("/"))
                os.makedirs(os.path.dirname(target), exist_ok=True)
                open(target, "wb").write(data)
                pin = (pinned or {}).get(name, data)
                files.append({"path": path, "bytes": len(pin), "sha256": hashlib.sha256(pin).hexdigest()})
            entry = {"id": voice, "language": "xx_XX", "repo": "test/voices", "revision": REV, "files": files,
                     "license": {"id": "MIT"}}
            json.dump(entry, open(os.path.join(catalogue, voice + ".json"), "w"))
            return entry

        def logged(fn, *args):
            out = io.StringIO()
            with contextlib.redirect_stderr(out):
                result = fn(*args)
            return result, out.getvalue()

        """;

    /// <summary>
    /// What ships parses, is pinned to a commit, and describes exactly the pair its id names — the
    /// same check <c>Dockerfile.tools</c> runs at build time, run here so it fails before a build.
    /// </summary>
    [PythonWorkerFact]
    public void EveryShippedVoiceIsPinnedAndNamesItsOwnPair() => Run("""
        os.environ["INFERHUB_PIPER_CATALOGUE"] = SHIPPED
        ids = sorted(os.path.basename(p)[:-5] for p in os.listdir(SHIPPED) if p.endswith(".json"))
        assert "bg_BG-dimitar-medium" in ids, ids
        for voice in ids:
            entry = pw.catalogue_entry(voice)
            assert entry["language"] and entry["license"]["id"], voice
            assert entry["repo"] == "rhasspy/piper-voices", voice
        assert pw.catalogue_entry("bg_BG-dimitar-medium")["language"] == "bg_BG"
        """);

    [PythonWorkerFact]
    public void AVoiceThatMatchesItsPinLandsAsAPairAndLeavesNoPartFiles() => Run("""
        publish("xx_XX-test-medium")
        pw.fetch_voice(pw.catalogue_entry("xx_XX-test-medium"))
        assert list(pw.voices()) == ["xx_XX-test-medium"], pw.voices()
        assert sorted(os.listdir(voices_dir)) == ["xx_XX-test-medium.onnx", "xx_XX-test-medium.onnx.json"], os.listdir(voices_dir)
        """);

    /// <summary>
    /// A substituted file never becomes a voice, and neither does its correct sibling: nothing is
    /// renamed until every file has matched.
    /// </summary>
    [PythonWorkerFact]
    public void AFileThatDoesNotMatchItsPinIsNeverInstalledAndNeitherIsItsSibling() => Run("""
        publish("xx_XX-bad-medium", pinned={"xx_XX-bad-medium.onnx.json": b"something else"})
        try:
            pw.fetch_voice(pw.catalogue_entry("xx_XX-bad-medium"))
            raise AssertionError("a mismatched file was accepted")
        except ValueError as error:
            message = str(error)
        assert "Not installed" in message and hashlib.sha256(b"{}").hexdigest() in message, message
        assert hashlib.sha256(b"something else").hexdigest() in message, message
        assert os.listdir(voices_dir) == [], os.listdir(voices_dir)
        assert pw.voices() == {}
        """);

    [PythonWorkerFact]
    public void OnlyNamedCatalogueVoicesArePlannedAndEveryOtherNameIsLoggedWithItsReason() => Run("""
        publish("xx_XX-one-medium")
        publish("xx_XX-two-medium")
        pw.fetch_voice(pw.catalogue_entry("xx_XX-two-medium"))

        os.environ["INFERHUB_SPEECH_VOICES"] = " xx_XX-one-medium, xx_XX-two-medium,nope,../evil,xx_XX-one-medium,"
        plan, log = logged(pw.plan_fetches)
        assert [e["id"] for e in plan] == ["xx_XX-one-medium"], plan
        assert "'nope' is not in the voice catalogue" in log, log
        assert "'../evil' is not a voice id" in log, log
        assert "voice xx_XX-two-medium" not in log and "voice xx_XX-one-medium" not in log, log

        os.environ["INFERHUB_SPEECH_VOICES"] = ""
        assert logged(pw.plan_fetches) == ([], "")

        os.environ["INFERHUB_SPEECH_VOICES"] = "xx_XX-one-medium"
        os.environ["INFERHUB_ALLOW_MODEL_DOWNLOAD"] = "0"
        plan, log = logged(pw.plan_fetches)
        assert plan == [], plan
        assert "Tools:AllowModelDownload is false" in log and f"/test/voices/resolve/{REV}/xx/xx_XX-one-medium/xx_XX-one-medium.onnx" in log, log
        """);

    /// <summary>
    /// Over the real protocol: the handshake says the voice is being fetched and offers nothing,
    /// and a later <c>ready</c> offers it with nothing left to fetch — no restart, no request.
    /// </summary>
    [PythonWorkerFact]
    public void TheWorkerSaysItIsFetchingThenDeclaresTheVoiceWhenItLands() => Run("""
        publish("xx_XX-live-medium")
        Handler.delay = 1.0
        env = dict(os.environ, INFERHUB_SPEECH_VOICES="xx_XX-live-medium")
        process = subprocess.Popen([sys.executable, "-u", WORKER], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, env=env, text=True)
        frames = queue.Queue()
        threading.Thread(target=lambda: [frames.put(json.loads(line)) for line in process.stdout], daemon=True).start()
        try:
            process.stdin.write(json.dumps({"type": "hello", "protocol": 1}) + "\n")
            process.stdin.flush()
            first = frames.get(timeout=30)
            assert first["type"] == "ready" and first.get("fetching") == ["xx_XX-live-medium"], first
            assert first["capabilities"] == [{"kind": "speak", "models": []}], first
            second = frames.get(timeout=30)
            assert second["type"] == "ready" and "fetching" not in second, second
            assert second["capabilities"] == [{"kind": "speak", "models": ["xx_XX-live-medium"]}], second
        finally:
            process.kill()
        """);

    private static void Run(string body)
    {
        var python = PythonWorkerTestGate.Interpreter!;
        var root = RepositoryRoot();
        var script = Path.Combine(Path.GetTempPath(), $"inferhub-voices-{Guid.NewGuid():N}.py");

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
            start.ArgumentList.Add(Path.Combine(root, "python", "tools", "piper_worker.py"));
            start.ArgumentList.Add(Path.Combine(root, "python", "voices"));
            start.Environment["PYTHONIOENCODING"] = "utf-8";

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            Assert.True(process.WaitForExit(120_000), "the voice catalogue script did not finish in two minutes");

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
