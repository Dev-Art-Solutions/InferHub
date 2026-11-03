slug: inferhub-3-88-firewall-rules-for-a-sealed-node
id: 6ae9e6adbde87999d24cc518 (EN visible, BG hidden)
title: InferHub 3.88: firewall rules for a sealed node, and a check that they hold
excerpt: A sealed node keeps InferHub off the internet, but only the operating system can stop everything else on the machine. Now the node prints the firewall rules that let it reach only its coordinator, and checks below InferHub that its machine reaches nothing else.

<p>InferHub 3.82 introduced the sealed node: a node that starts with no door to the internet open and downloads only through its coordinator. That is a promise about one program. Every other program on the same machine, and an inference engine's own downloader, can still reach anywhere the operating system lets them. The thing that can stop them is the machine's firewall.</p>

<p>InferHub 3.88 does two things about that. The node prints the firewall rules that match it, and a sealed node checks, below InferHub's own code, whether the machine really reaches nothing outside.</p>

<h2>Rules from the node's own configuration</h2>

<pre><code class="json">InferHub.Node firewall powershell          # Windows Defender Firewall, run as Administrator
InferHub.Node firewall nftables            # Linux: save the output, then nft -f it as root
InferHub.Node firewall nftables --no-dns   # the coordinator is an IP or in /etc/hosts</code></pre>

<p>The node reads its coordinator addresses, resolves them, and prints rules that allow outbound connections to those addresses and ports, optionally DNS, keep loopback and replies, and drop everything else. The top of the output says what was resolved and how to undo it. If a coordinator name does not resolve, the output says so instead of quietly leaving its rule out. The same text is available from the node's control API, so a desktop app or a script can fetch it.</p>

<p>The node prints the rules and never applies them. They change the network of the whole machine, not just InferHub, so that decision belongs to the person who owns the machine.</p>

<h2>A check below InferHub</h2>

<p>A sealed node now tries a plain TCP connection to a few addresses outside every private network, by default three public DNS providers on port 443, when it starts and every fifteen minutes. Nothing is sent. If any connection succeeds, the operating system let it out, and the machine is not sealed, whatever InferHub itself does. The probes are IP addresses on purpose: a hostname would need DNS, and the DNS the rules allow could make a closed machine look open.</p>

<p>The node says what it found in its log and on its control API, and tells its coordinator. The coordinator shows it per node on its status page, counts it in its metrics (zero means sealed), and logs a warning when a sealed node can reach outside. It does not refuse the node: the machine is still its owner's, and refusing work would punish the rest of the fleet for something only the owner can fix.</p>

<h2>What checking the output found</h2>

<p>The unit tests checked the text of both rule formats and passed. Then we fed the generated nftables file to <code>nft -c</code>, which validates rules without applying them, and it rejected every single line. The node had printed the rules on Windows, where .NET ends each line with a carriage return and a line feed, and nftables reads the carriage return as junk. The nftables output now always uses plain line feeds, and a test asserts it. The PowerShell script was checked with PowerShell's own parser, with no errors.</p>

<h2>Checked live</h2>

<p>A coordinator and a sealed node ran on a machine that does have internet, so the honest expectation was "open", and that is what came back: all three probes connected. The node logged it, the coordinator logged that the sealed node could reach those addresses itself, the status page listed them, and the metric said 3.</p>

<p>What was not done is applying the rules to a real machine and watching the check turn to "closed": doing that here would have cut the test machine off. The closed result is tested against refused local ports. There are no macOS rules yet, and none for the coordinator's own host, which is the one machine meant to reach outside.</p>

<p>A node that is not sealed probes nothing and reports nothing. The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.88.0">v3.88.0 release</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_sealed_firewall">inferhub.devart.solutions</a>.</p>
