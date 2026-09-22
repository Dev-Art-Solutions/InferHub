namespace InferHub.Node.Tools;

/// <summary>
/// Phase 83. Turns a manifest's resolved argv (phase-79 D1's platform switch has already run by the
/// time this sees it) into the argv the node actually spawns — unchanged for
/// <see cref="ToolSandboxMode.None"/>, wrapped in <c>bwrap</c> for
/// <see cref="ToolSandboxMode.Bubblewrap"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Docker-socket wrapping was considered and rejected.</b> Mounting <c>docker.sock</c> into the
/// node so it could <c>docker run</c> each tool would isolate the tool, but the socket itself is
/// root-equivalent on the host — a process that can reach it can mount the host filesystem into a
/// throwaway container and read anything. That trades "an untrusted tool has the node's filesystem"
/// (D7) for "an untrusted tool has the *host's* filesystem", which is a worse privilege-escalation
/// surface than the problem it claims to solve. <c>bwrap</c> needs no daemon, no socket and no root:
/// it is a single setuid-or-unprivileged-namespaces binary the node's own user can invoke directly,
/// and its blast radius is the namespaces it constructs, not a control plane for every container on
/// the box.
/// </para>
/// <para>
/// <b>The binds are derived, not a universal set.</b> Every path in the manifest's own <c>command</c>
/// array that resolves to a real file or directory, plus its <c>workdir</c>, is bound read-only —
/// for a manifest whose command is <c>["/opt/inferhub/venv/bin/python", "-u",
/// "/opt/inferhub/tools/whisper_worker.py"]</c> that is the venv's <c>bin</c> directory and the
/// script's own directory, collapsed to <c>/opt/inferhub</c> once one is found to contain the other
/// — alongside the handful of system directories any interpreter needs to start at all: <c>/usr</c>
/// and <c>/etc</c> bound plainly, and <c>/bin</c>/<c>/lib</c>/<c>/lib64</c>/<c>/sbin</c> recreated as
/// symlinks into <c>/usr</c> (<c>--symlink</c>, not <c>--ro-bind</c>) on the merged-usr layout every
/// distribution this project ships from actually uses — see <c>MergedUsrSymlinkCandidates</c>.
/// Guessing one fixed bind list for every tool that will ever exist would either be too narrow for a
/// real worker (missing a shared library it dlopens) or
/// too wide for a small one (handing a lightweight tool the whole filesystem back through a
/// different door) — deriving it from what the manifest itself already declares keeps the two in
/// lockstep by construction.
/// </para>
/// <para>
/// <b>Read-write is exactly <c>Tools:ScratchDirectory</c>, and nothing else.</b> That is the one
/// place a worker is meant to write (phase-41 D5) — the per-request subdirectories it creates live
/// directly under it — so this is not a new grant, it is the existing one made structural instead of
/// merely a convention a well-behaved worker follows.
/// </para>
/// <para>
/// <b><c>--die-with-parent</c> is NOT in the built argv, and that is a deviation from the phase
/// brief, found by actually running this against a real child under bubblewrap rather than by
/// parsing.</b> Bubblewrap implements it with <c>PR_SET_PDEATHSIG</c>, which Linux delivers when
/// the <em>specific OS thread</em> that called <c>fork()</c> exits — not when the process as a
/// whole does. <c>System.Diagnostics.Process.Start</c> on Linux can perform that fork from a
/// .NET thread-pool thread, which is free to be recycled and torn down moments later; when it
/// is, the kernel delivers the signal immediately and the sandboxed worker is SIGKILLed before it
/// can even send <c>hello</c> — reliably, not intermittently, in this project's own Mesh test run
/// inside a real Linux container. Bubblewrap's own answer to exactly this class of multi-threaded
/// parent is <c>--sync-fd</c> (hold a pipe open; bwrap exits on EOF), but wiring an arbitrary extra
/// inherited file descriptor through <see cref="System.Diagnostics.ProcessStartInfo"/> has no
/// portable, shell-free path in this runtime without native interop this project does not otherwise
/// need — rule 5's bar for a new dependency, applied to a P/Invoke surface instead of a package.
/// **What this leaves uncovered:** a node that crashes (a segfault, a <c>kill -9</c>) rather than
/// exiting cleanly can leave an orphaned sandboxed worker running. The ordinary shutdown path is
/// unaffected — <c>ToolWorkerProcess.StopAsync</c>/<c>TerminateAsync</c> already
/// <c>Process.Kill(entireProcessTree: true)</c> the whole tree (phase-41 D6) — so this is a narrow
/// gap on the unclean-exit path specifically, named here rather than silently dropped, alongside
/// seccomp and UID-namespace remapping as the next thing a future phase should close.
/// </para>
/// <para>
/// <b>Not covered, named rather than implied away:</b> seccomp syscall filtering and UID-namespace
/// remapping are both out of scope for this phase. A sandboxed worker still runs as the node's own
/// uid (no <c>--unshare-user</c> — that needs either a setuid <c>bwrap</c> or
/// <c>kernel.unprivileged_userns_clone</c>, neither of which this phase requires the deployment to
/// have) and can still make any syscall the kernel allows a process to make. What changed is
/// <em>what it can see and reach</em> — the filesystem outside its declared binds and the network
/// unless asked for — not what it can <em>do</em> with what it already has. See phase-83 D1.
/// </para>
/// </remarks>
internal static class ToolSandboxing
{
    /// <summary>Real directories at these paths (not symlinks into <c>/usr</c>) are simply bound.</summary>
    private static readonly string[] RealDirectoryCandidates = ["/usr", "/etc"];

    /// <summary>
    /// On every merged-<c>/usr</c> distribution (Debian/Ubuntu since ~2017, which is what the
    /// project's own Dockerfiles are built from) these are <b>symlinks into <c>/usr</c></b>, not
    /// directories — <c>readlink /bin</c> answers <c>usr/bin</c>. <c>--ro-bind /bin /bin</c> against
    /// a symlink does not recreate the symlink in bwrap's new root, so anything resolved through it
    /// (a dynamic loader at <c>/lib64/ld-linux-x86-64.so.2</c>, a script's <c>/bin/sh</c> shebang)
    /// comes back <c>ENOENT</c> even though <c>/usr/lib64/…</c> is right there. Found the hard way —
    /// see phase-83's "run it for real" verification — and it is why these need <c>--symlink</c>
    /// rather than <c>--ro-bind</c>.
    /// </summary>
    private static readonly string[] MergedUsrSymlinkCandidates = ["/bin", "/lib", "/lib64", "/sbin"];

    public static string[] BuildArgv(ToolManifest manifest, string scratchDirectory)
    {
        if (manifest.Sandbox.Mode is not ToolSandboxMode.Bubblewrap)
        {
            return manifest.Command.ToArray();
        }

        var argv = new List<string> { "bwrap" };

        foreach (var path in RealDirectoryCandidates.Where(Directory.Exists))
        {
            argv.Add("--ro-bind");
            argv.Add(path);
            argv.Add(path);
        }

        foreach (var path in MergedUsrSymlinkCandidates)
        {
            if (TryResolveSymlinkTarget(path, out var target))
            {
                argv.Add("--symlink");
                argv.Add(target);
                argv.Add(path);
            }
            else if (Directory.Exists(path))
            {
                // Not every distribution uses merged-usr (older glibc bases still ship real
                // directories here) — bind it plainly rather than assuming the symlink shape.
                argv.Add("--ro-bind");
                argv.Add(path);
                argv.Add(path);
            }
        }

        foreach (var path in OwnCommandBinds(manifest))
        {
            argv.Add("--ro-bind");
            argv.Add(path);
            argv.Add(path);
        }

        // A worker needs *somewhere* to see live processes and devices to run at all; --proc and
        // --dev are bwrap's own fresh, namespaced mounts, not a bind of the host's. --tmpfs /tmp is
        // the same idea for scratch a library writes without asking (a lock file, a cache directory
        // an interpreter assumes exists) — it is discarded with the mount namespace, never the
        // node's own /tmp.
        argv.Add("--proc");
        argv.Add("/proc");
        argv.Add("--dev");
        argv.Add("/dev");
        argv.Add("--tmpfs");
        argv.Add("/tmp");

        // The one read-write grant, added LAST and deliberately after --tmpfs /tmp: on a bare-metal
        // or dev deployment `Tools:ScratchDirectory` can itself resolve under /tmp (as it does in
        // every test fixture in this suite), and bwrap's binds apply in argv order — an earlier bind
        // under /tmp would otherwise be shadowed the moment the later `--tmpfs /tmp` mounts over it.
        // Found running this for real: a worker started, then failed to see its own scratch
        // directory, because the tmpfs had silently eaten it.
        Directory.CreateDirectory(scratchDirectory);
        argv.Add("--bind");
        argv.Add(scratchDirectory);
        argv.Add(scratchDirectory);

        argv.Add("--unshare-pid");

        // Phase-83 D2: off unless the manifest says so, and the manifest saying so is the whole
        // grant — there is no second place that widens it back.
        if (!manifest.Sandbox.Network)
        {
            argv.Add("--unshare-net");
        }

        argv.Add("--");
        argv.AddRange(manifest.Command);

        return argv.ToArray();
    }

    /// <summary>
    /// The fully-resolved absolute target of a top-level symlink such as <c>/bin -&gt; usr/bin</c>,
    /// or false when the path is not a symlink at all (a non-merged-usr distribution, or the path
    /// does not exist on this box).
    /// </summary>
    private static bool TryResolveSymlinkTarget(string path, out string target)
    {
        target = string.Empty;

        try
        {
            var info = new DirectoryInfo(path);

            if (!info.Exists || info.LinkTarget is null)
            {
                return false;
            }

            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true);

            if (resolved is null)
            {
                return false;
            }

            target = resolved.FullName;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Every directory the manifest's own paths point into — not a guess at what a tool "probably"
    /// needs, but exactly what its own <c>command</c> array and <c>workdir</c> already name.
    /// </summary>
    /// <remarks>
    /// <b>Every argv element that resolves to a real file or directory on this box is bound, not
    /// only element 0.</b> A venv's <c>python</c> is one path; the script it runs, and any data
    /// directory an argument names, are others — <c>whisper.json</c>'s command is
    /// <c>["/opt/.../venv/bin/python", "-u", "/opt/.../whisper_worker.py"]</c>, and the second path
    /// is not under the first's directory. Binding element 0 alone would be right for that one
    /// manifest and wrong for the next one whose script lives somewhere else.
    /// </remarks>
    private static IEnumerable<string> OwnCommandBinds(ToolManifest manifest)
    {
        var candidates = new List<string>();

        foreach (var argument in manifest.Command)
        {
            if (!Path.IsPathRooted(argument))
            {
                continue;
            }

            if (Directory.Exists(argument))
            {
                candidates.Add(Path.GetFullPath(argument));
            }
            else if (File.Exists(argument))
            {
                candidates.Add(VenvRootOrDirectory(Path.GetFullPath(argument)));
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.WorkingDirectory) && Directory.Exists(manifest.WorkingDirectory))
        {
            candidates.Add(Path.GetFullPath(manifest.WorkingDirectory));
        }

        // Collapse to the outermost paths only — binding both a directory and something already
        // inside it is harmless to bwrap but noise in the argv a reader has to check.
        var distinct = candidates.Distinct(StringComparer.Ordinal).ToList();

        return distinct
            .Where(path => !distinct.Any(other =>
                !string.Equals(other, path, StringComparison.Ordinal) && IsAncestor(other, path)))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    /// <summary>
    /// <c>venv/bin/python</c> needs <c>venv/lib</c> (site-packages) and <c>venv/pyvenv.cfg</c>, not
    /// only its own directory — a binary's directory alone is the right bind for an ordinary
    /// executable, but a virtualenv's own layout means "the directory this file is in" and "what
    /// this file needs to run" are two different answers. <c>pyvenv.cfg</c> is the marker every venv
    /// tool (including the one that created it) already relies on, so climbing to find it is
    /// detecting the shape rather than assuming it.
    /// </summary>
    private static string VenvRootOrDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);

        for (var i = 0; directory is not null && i < 3; i++)
        {
            if (File.Exists(Path.Combine(directory, "pyvenv.cfg")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return Path.GetDirectoryName(filePath) ?? filePath;
    }

    private static bool IsAncestor(string ancestor, string path)
    {
        var normalizedAncestor = ancestor.TrimEnd('/', '\\');
        var normalizedPath = path.TrimEnd('/', '\\');

        return normalizedPath.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || normalizedPath.StartsWith(normalizedAncestor + '/', StringComparison.Ordinal);
    }
}
