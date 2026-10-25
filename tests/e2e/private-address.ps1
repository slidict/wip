# Shared by the scan scenarios, which each resolve a target address out of the container and
# then hand it to a scanner. The scan path already cannot reach a public address -- the sandbox
# is on an `internal: true` network and the engine refuses to start without one -- so this is
# not the boundary. It is the assertion that these fixtures still mean what they say: an edit
# that pointed a scenario at something outside the isolated network, by accident or otherwise,
# would otherwise run and pass rather than stop here.
#
# 169.254.0.0/16 is refused explicitly and not merely absent from the allowed set: it is the
# link-local range that carries the cloud metadata endpoint, which is one of the destinations
# the isolation work exists to keep a scanner away from.

function Test-PrivateIPv4([string] $Address) {
    $parsed = [System.Net.IPAddress]::Any
    if (-not [System.Net.IPAddress]::TryParse($Address, [ref] $parsed)) { return $false }
    if ($parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $octets = $parsed.GetAddressBytes()
    if ($octets[0] -eq 169 -and $octets[1] -eq 254) { return $false }
    if ($octets[0] -eq 10) { return $true }
    if ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) { return $true }
    if ($octets[0] -eq 192 -and $octets[1] -eq 168) { return $true }
    return $false
}

function Assert-PrivateIPv4([string] $Address, [string] $What) {
    if (-not (Test-PrivateIPv4 $Address)) {
        throw "${What}: '$Address' is not an RFC 1918 address on the isolated network. A scan fixture must never be pointed outside it."
    }
}
