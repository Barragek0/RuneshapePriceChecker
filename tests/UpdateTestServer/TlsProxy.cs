using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

if (args.Length < 2)
{
    Console.WriteLine("Usage: TlsProxy <listenPort> <forwardPort> [certSubject]");
    Console.WriteLine("  e.g.: TlsProxy 443 8098 api.github.com");
    return;
}

var listenPort = int.Parse(args[0], CultureInfo.InvariantCulture);
var forwardPort = int.Parse(args[1], CultureInfo.InvariantCulture);
var certSubject = args.Length > 2 ? args[2] : "api.github.com";

// Load cert from CurrentUser\My
using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
store.Open(OpenFlags.ReadOnly);
var cert = store.Certificates
    .Find(X509FindType.FindBySubjectName, certSubject, false)
    .OfType<X509Certificate2>()
    .FirstOrDefault();

if (cert is null)
{
    Console.WriteLine($"Certificate for '{certSubject}' not found in CurrentUser\\My.");
    Console.WriteLine("Run the cert generation script first.");
    return;
}

Console.WriteLine($"Using cert: {cert.Subject} (thumbprint: {cert.Thumbprint})");

var listener = new TcpListener(IPAddress.Any, listenPort);
listener.Start();
Console.WriteLine($"TLS proxy listening on :{listenPort} -> localhost:{forwardPort}");

AddHostsEntry(certSubject);
Console.WriteLine("Press Ctrl+C to stop.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    RemoveHostsEntry(certSubject);
};

try
{
    while (!cts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        _ = Task.Run(() => HandleClient(client, forwardPort, cert, cts.Token));
    }
}
catch (OperationCanceledException) { }
finally
{
    listener.Stop();
}

static async Task HandleClient(TcpClient client, int forwardPort, X509Certificate2 cert, CancellationToken ct)
{
    try
    {
        using var sslStream = new SslStream(client.GetStream(), false);
        await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false
        }, ct);

        // Read the HTTP request from the TLS stream
        using var reader = new StreamReader(sslStream, Encoding.ASCII, false, 4096, true);
        var requestLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(requestLine)) return;

        var parts = requestLine.Split(' ');
        var method = parts[0];
        var path = parts[1];

        // Read headers
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? headerLine;
        while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync(ct)))
        {
            var colonIdx = headerLine.IndexOf(':');
            if (colonIdx > 0)
                headers[headerLine[..colonIdx].Trim()] = headerLine[(colonIdx + 1)..].Trim();
        }

        // Read body if Content-Length is present
        byte[]? body = null;
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var contentLength) && contentLength > 0)
        {
            body = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = await sslStream.ReadAsync(body.AsMemory(read, contentLength - read), ct);
                if (n == 0) break;
                read += n;
            }
        }

        Console.WriteLine($"{method} {path}");

        // Forward to test server
        using var forwardClient = new TcpClient();
        await forwardClient.ConnectAsync("localhost", forwardPort, ct);
        var forwardStream = forwardClient.GetStream();

        var requestBytes = Encoding.ASCII.GetBytes($"{method} {path} HTTP/1.1\r\n");
        await forwardStream.WriteAsync(requestBytes, ct);

        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
                await forwardStream.WriteAsync(Encoding.ASCII.GetBytes($"Host: localhost:{forwardPort}\r\n"), ct);
            else
                await forwardStream.WriteAsync(Encoding.ASCII.GetBytes($"{key}: {value}\r\n"), ct);
        }
        await forwardStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);

        if (body is not null)
            await forwardStream.WriteAsync(body, ct);

        // Read response and forward back to client
        var responseBuffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await forwardStream.ReadAsync(responseBuffer, ct)) > 0)
        {
            await sslStream.WriteAsync(responseBuffer.AsMemory(0, bytesRead), ct);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Proxy error: {ex.Message}");
    }
    finally
    {
        client.Dispose();
    }
}

static void AddHostsEntry(string hostname)
{
    try
    {
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        var entry = $"127.0.0.1 {hostname}";
        var hosts = File.ReadAllText(hostsPath);
        if (hosts.Contains(entry))
        {
            Console.WriteLine($"Hosts entry already exists: {entry}");
            return;
        }

        Console.WriteLine($"Adding hosts entry: {entry} (requires admin)");
        var psi = new ProcessStartInfo("powershell", $"-Command \"Add-Content -Path '{hostsPath}' -Value '{entry}'\"")
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi)?.WaitForExit(5000);
        Console.WriteLine("Hosts entry added.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to add hosts entry: {ex.Message}");
    }
}

static void RemoveHostsEntry(string hostname)
{
    try
    {
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        var entry = $"127.0.0.1 {hostname}";
        var lines = File.ReadAllLines(hostsPath).ToList();
        var removed = lines.RemoveAll(l => string.Equals(l.Trim(), entry, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;

        Console.WriteLine($"Removing hosts entry: {entry} (requires admin)");
        var tempPath = Path.GetTempFileName();
        File.WriteAllLines(tempPath, lines);
        var psi = new ProcessStartInfo("powershell", $"-Command \"Copy-Item -Path '{tempPath}' -Destination '{hostsPath}' -Force\"")
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi)?.WaitForExit(5000);
        try { File.Delete(tempPath); } catch { }
        Console.WriteLine("Hosts entry removed.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to remove hosts entry: {ex.Message}");
    }
}
