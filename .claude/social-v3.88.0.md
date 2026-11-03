# v3.88.0 social copy (unposted)

## Facebook

InferHub 3.88: firewall rules for a sealed node, and a check that they hold.

A sealed node keeps InferHub off the internet, but only the operating system can stop everything else on the machine. Now the node prints Windows Defender Firewall or nftables rules from its own configuration: out only to its coordinator, everything else dropped. And a sealed node checks below InferHub, with a plain TCP connect to a few public addresses, whether its machine can still reach outside, and tells its coordinator.

Checking the output found a real bug first: rules printed on Windows had CRLF line ends, and nftables rejected every line. Fixed, with a test.

https://blog.devart.solutions/blog/inferhub-3-88-firewall-rules-for-a-sealed-node

## X

InferHub 3.88: a sealed node prints the firewall rules that match it, and checks below InferHub that its machine reaches nothing but its coordinator.

https://blog.devart.solutions/blog/inferhub-3-88-firewall-rules-for-a-sealed-node
