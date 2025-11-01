using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class TcpJsonServer
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public TcpJsonServer(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
    }

    public async Task RunAsync()
    {
        _listener.Start();
        Console.WriteLine($"Listening on {_listener.LocalEndpoint}");

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client, _cts.Token); // 並列処理で各クライアント対応
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("サーバー停止中...");
        }
        finally
        {
            _listener.Stop();
        }
    }

    public void Stop() => _cts.Cancel();

    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint}");

        await using var stream = client.GetStream();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // --- 4バイト長を受信 ---
                var lenBuf = new byte[4];
                if (!await ReadExactAsync(stream, lenBuf, ct)) break;

                int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (length <= 0 || length > 10_000_000)
                    throw new InvalidDataException($"Invalid message length: {length}");

                // --- 本文を受信 ---
                var body = new byte[length];
                if (!await ReadExactAsync(stream, body, ct)) break;

                var json = Encoding.UTF8.GetString(body);
                var msg = JsonSerializer.Deserialize<Message>(json);

                Console.WriteLine($"[{DateTime.Now:T}] 受信: {msg?.Text}");

                // --- 応答を送信 ---
                var response = new Message { Text = $"受け取りました: {msg?.Text}" };
                var respBytes = JsonSerializer.SerializeToUtf8Bytes(response);

                var respLen = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(respLen, respBytes.Length);

                await stream.WriteAsync(respLen, ct);
                await stream.WriteAsync(respBytes, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Console.WriteLine($"Client error: {ex.Message}");
        }
        finally
        {
            client.Close();
            Console.WriteLine("Client disconnected");
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public class Message
    {
        public string? Text { get; set; }
    }

    public static async Task Main()
    {
        // ←ここで "Any" ではなく Loopback（127.0.0.1）限定
        var server = new TcpJsonServer(IPAddress.Loopback, 5000);

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            server.Stop();
        };

        await server.RunAsync();
    }
}
