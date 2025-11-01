using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

public class TcpJsonClient
{
    public class Message { public string? Text { get; set; } }

    public static async Task Main()
    {
        using var client = new TcpClient();
        // "127.0.0.1" の代わりに IPAddress.Loopback を使用
        await client.ConnectAsync(IPAddress.Loopback, 5000);
        client.NoDelay = true;

        await using var stream = client.GetStream();

        // メッセージ送信
        var msg = new Message { Text = "こんにちは、サーバー！(async)" };
        var payload = JsonSerializer.SerializeToUtf8Bytes(msg);

        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        await stream.WriteAsync(len);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();

        // 応答受信
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf)) return;
        int respLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        var respBuf = new byte[respLen];
        if (!await ReadExactAsync(stream, respBuf)) return;

        var resp = JsonSerializer.Deserialize<Message>(Encoding.UTF8.GetString(respBuf));
        Console.WriteLine("サーバー応答: " + resp?.Text);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
