#requires -Version 5.1
<#
.SYNOPSIS
  Single-user loopback HTTP transport without HttpListener / HTTP.sys URL ACLs.
.DESCRIPTION
  TCP binds ONLY to 127.0.0.1. This is a restricted HTTP/1.1
  adapter for the existing Cascade request router. It is not a public server
  and does not disable endpoint security controls.
#>
function Start-CascadeLoopbackTransport {
    [CmdletBinding()]
    param([ValidateRange(1024,65535)][int]$Port)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
        return $listener
    } catch [System.Net.Sockets.SocketException] {
        if ($_.Exception.SocketErrorCode -eq [System.Net.Sockets.SocketError]::AddressAlreadyInUse) {
            throw "Local port $Port is already in use. Close the other Cascade instance or choose another port."
        }
        throw ("Cannot open the local TCP listener at 127.0.0.1:{0}: {1}. Check approved endpoint/network controls." -f $Port,$_.Exception.Message)
    }
}

function Read-CascadeLoopbackContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Net.Sockets.TcpListener]$Listener,
        [ValidateRange(1024,65535)][int]$Port
    )
    $client = $Listener.AcceptTcpClient()
    try {
        $client.ReceiveTimeout = 15000
        $client.SendTimeout = 15000
        $net = $client.GetStream()
        # Strictly bounded HTTP/1.1 request; no chunked encoding or keepalive.
        $raw = New-Object 'System.Collections.Generic.List[byte]'
        while ($true) {
            $b = $net.ReadByte()
            if ($b -lt 0) { throw 'Client disconnected before HTTP headers.' }
            $raw.Add([byte]$b)
            $n = $raw.Count
            if ($n -gt 16384) { throw 'HTTP headers exceeded the 16 KB limit.' }
            if ($n -ge 4 -and $raw[$n-4] -eq 13 -and $raw[$n-3] -eq 10 -and
                $raw[$n-2] -eq 13 -and $raw[$n-1] -eq 10) { break }
        }
        $lines = [Text.Encoding]::ASCII.GetString($raw.ToArray()) -split "\r\n"
        if ($lines.Count -lt 2 -or
            $lines[0] -notmatch '^(GET|POST|HEAD|OPTIONS) (/[^\s]*) HTTP/1\.[01]$') {
            throw 'Unsupported HTTP request line.'
        }
        $method = [string]$Matches[1]
        $target = [string]$Matches[2]
        $headers = New-Object System.Collections.Specialized.NameValueCollection
        foreach ($line in @($lines | Select-Object -Skip 1)) {
            if (-not $line) { break }
            $colon = $line.IndexOf(':')
            if ($colon -le 0) { throw 'Invalid HTTP header.' }
            $key = $line.Substring(0,$colon).Trim()
            $value = $line.Substring($colon+1).Trim()
            if ($key -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid HTTP header name.' }
            if ($null -ne $headers[$key]) { throw 'Duplicate HTTP header not supported.' }
            $headers.Add($key,$value)
        }
        $hostHeader = [string]$headers['Host']
        if ($hostHeader -notin @("localhost:$Port","127.0.0.1:$Port",'localhost','127.0.0.1')) {
            throw 'HTTP Host is not an approved loopback host.'
        }
        if ($headers['Transfer-Encoding']) { throw 'Transfer-Encoding is not supported.' }
        $size = [long]0
        if ($null -ne $headers['Content-Length']) {
            if (-not [long]::TryParse($headers['Content-Length'],[ref]$size)) {
                throw 'Invalid Content-Length.'
            }
        } elseif ($method -eq 'POST') {
            throw 'POST requests require Content-Length.'
        }
        if ($size -lt 0 -or $size -gt 16384) { throw 'Request body exceeded the 16 KB limit.' }
        if ($headers['Expect'] -eq '100-continue' -and $size -gt 0) {
            $provisional = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 100 Continue`r`n`r`n")
            $net.Write($provisional,0,$provisional.Length)
        }
        $body = New-Object System.IO.MemoryStream
        $buffer = New-Object byte[] 4096
        $remaining = $size
        while ($remaining -gt 0) {
            $read = $net.Read($buffer,0,[int][Math]::Min($remaining,$buffer.Length))
            if ($read -le 0) { throw 'HTTP request body ended early.' }
            $body.Write($buffer,0,$read)
            $remaining -= $read
        }
        $body.Position = 0
        $url = [Uri]("http://127.0.0.1:$Port$target")
        $request = [pscustomobject]@{
            Url = $url
            HttpMethod = $method
            Headers = $headers
            ContentLength64 = $size
            InputStream = $body
            RemoteEndPoint = $client.Client.RemoteEndPoint
        }
        $response = [pscustomobject]@{
            StatusCode = 200
            ContentType = 'application/octet-stream'
            ContentLength64 = 0
            Headers = (New-Object System.Collections.Specialized.NameValueCollection)
            OutputStream = (New-Object System.IO.MemoryStream)
            NetworkStream = $net
            Client = $client
            Closed = $false
        }
        $response | Add-Member -MemberType ScriptMethod -Name Close -Value {
            if ($this.Closed) { return }
            $this.Closed = $true
            try {
                $payload = $this.OutputStream.ToArray()
                $reason = switch ([int]$this.StatusCode) {
                    200 {'OK'} 400 {'Bad Request'} 403 {'Forbidden'}
                    404 {'Not Found'} 405 {'Method Not Allowed'}
                    413 {'Payload Too Large'} 500 {'Internal Server Error'}
                    503 {'Service Unavailable'} default {'Response'}
                }
                $builder = New-Object System.Text.StringBuilder
                [void]$builder.Append("HTTP/1.1 $($this.StatusCode) $reason`r`n")
                [void]$builder.Append("Content-Type: $($this.ContentType)`r`n")
                [void]$builder.Append("Content-Length: $($payload.Length)`r`n")
                [void]$builder.Append("Connection: close`r`n")
                foreach ($key in @($this.Headers.AllKeys)) {
                    $value = [string]$this.Headers[$key]
                    if ($key -match '^[A-Za-z0-9-]+$' -and $value -notmatch '[\r\n]') {
                        [void]$builder.Append("$($key): $value`r`n")
                    }
                }
                [void]$builder.Append("`r`n")
                $first = [Text.Encoding]::ASCII.GetBytes($builder.ToString())
                $this.NetworkStream.Write($first,0,$first.Length)
                if ($payload.Length -gt 0) {
                    $this.NetworkStream.Write($payload,0,$payload.Length)
                }
                $this.NetworkStream.Flush()
            } finally {
                $this.OutputStream.Dispose()
                $this.Client.Close()
            }
        } -Force
        return [pscustomobject]@{ Request=$request; Response=$response }
    } catch {
        $client.Close()
        # Do not log request headers or bodies.
        throw
    }
}
