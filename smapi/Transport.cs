using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CorticoStardew;

public sealed class ModConfig
{
    public int Port { get; set; } = 24983;
    public string Token { get; set; } = "";
}

public sealed class Transport : IDisposable
{
    private readonly ModConfig config;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object writeLock = new();
    private StreamWriter? writer;
    private TcpClient? client;
    public readonly ConcurrentQueue<(int Generation, JsonElement Message)> Incoming = new();
    public volatile bool Connected;
    public int Generation { get; private set; }

    public Transport(ModConfig config) { this.config = config; }

    public async Task Run()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                using var connection = new TcpClient();
                client = connection;
                await connection.ConnectAsync("127.0.0.1", config.Port, shutdown.Token);
                connection.NoDelay = true;
                connection.SendTimeout = 1000;
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var output = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                lock (writeLock) writer = output;
                Generation++;
                Send(new { type = "hello", protocol = 1, token = config.Token });
                while (!shutdown.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(shutdown.Token);
                    if (line == null || line.Length > 65536) break;
                    using var document = JsonDocument.Parse(line);
                    var message = document.RootElement;
                    if (!Connected)
                    {
                        if (message.GetProperty("type").GetString() != "welcome" || message.GetProperty("protocol").GetInt32() != 1) break;
                        Connected = true;
                    }
                    else
                    {
                        if (Incoming.Count >= 16) break;
                        Incoming.Enqueue((Generation, message.Clone()));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            finally
            {
                Connected = false;
                lock (writeLock) writer = null;
                client = null;
            }
            try { await Task.Delay(2000, shutdown.Token); } catch (OperationCanceledException) { }
        }
    }

    public void Send(object value)
    {
        lock (writeLock)
        {
            try { writer?.WriteLine(JsonSerializer.Serialize(value)); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Connected = false; client?.Close(); }
        }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        client?.Close();
    }
}
