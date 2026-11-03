using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;

namespace STS2Mobile.FullGame;

/// <summary>
/// <c>STS2_CHECK_RELAY_RUNTIME=&lt;ws address&gt;</c>: checks, under this app's Mono, what QuickMatch's relay and voice call
/// (AES-GCM with 32-byte keys and 16-byte tags, HKDF-SHA256, HMAC-SHA256, random bytes) against known answers, then one
/// WebSocket echo opened through an <see cref="HttpMessageInvoker"/> the way the relay connection will open it.
/// A test switch: it logs and changes nothing.
/// </summary>
internal static class RelayRuntimeCheck
{
    public static async Task RunAsync(string address)
    {
        GD.Print($"Relay runtime check: AesGcm.IsSupported={AesGcm.IsSupported}.");
        Report("random bytes", () => RandomNumberGenerator.GetBytes(32).Length == 32);
        // RFC 4231 test case 2.
        Report("HMAC-SHA256", () => Convert.ToHexStringLower(HMACSHA256.HashData("Jefe"u8, "what do ya want for nothing?"u8))
            == "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843");
        // RFC 5869 test case 1.
        Report("HKDF-SHA256", () => Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, Ikm(),
            42, Convert.FromHexString("000102030405060708090a0b0c"), Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9")))
            == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");
        // The GCM specification's test case 14: a zero 256-bit key, nonce and block.
        Report("AES-256-GCM known answer", () =>
        {
            using var cipher = new AesGcm(new byte[32], 16);
            byte[] sealedText = new byte[16], tag = new byte[16];
            cipher.Encrypt(new byte[12], new byte[16], sealedText, tag);
            return Convert.ToHexStringLower(sealedText) == "cea7403d4d606b6e074ec5d3baf39d18" && Convert.ToHexStringLower(tag) == "d0d1c8a799996bf0265b98b5d48ab919";
        });
        Report("AES-256-GCM round trip and tamper", () =>
        {
            byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, RandomNumberGenerator.GetBytes(32), 32, salt: null, info: BitConverter.GetBytes(76561197960265728UL));
            using var cipher = new AesGcm(key, 16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12), plain = "QuickMatch relay check"u8.ToArray();
            byte[] sealedText = new byte[plain.Length], tag = new byte[16], opened = new byte[plain.Length];
            cipher.Encrypt(nonce, plain, sealedText, tag, "header"u8);
            cipher.Decrypt(nonce, sealedText, tag, opened, "header"u8);
            if (!opened.AsSpan().SequenceEqual(plain)) return false;
            sealedText[0] ^= 1;
            try { cipher.Decrypt(nonce, sealedText, tag, opened, "header"u8); return false; }
            catch (AuthenticationTagMismatchException) { return true; }
        });
        await WebSocketEchoAsync(address);
    }

    private static byte[] Ikm()
    {
        byte[] ikm = new byte[22];
        Array.Fill(ikm, (byte)0x0b);
        return ikm;
    }

    private static void Report(string what, Func<bool> check)
    {
        try { GD.Print($"Relay runtime check: {what}: {(check() ? "pass" : "FAIL (wrong result)")}."); }
        catch (Exception error) { GD.Print($"Relay runtime check: {what}: FAIL ({error.GetType().Name}: {error.Message})."); }
    }

    private static async Task WebSocketEchoAsync(string address)
    {
        try
        {
            using var invoker = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
            using var socket = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri(address), invoker, timeout.Token);
            byte[] frame = [(byte)'D', 1, 2, 3, 4];
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, timeout.Token);
            byte[] buffer = new byte[64];
            var received = await socket.ReceiveAsync(buffer, timeout.Token);
            bool echoed = received.MessageType == WebSocketMessageType.Binary && buffer.AsSpan(0, received.Count).SequenceEqual(frame);
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", close.Token);
            GD.Print($"Relay runtime check: WebSocket through an invoker: {(echoed ? "pass" : "FAIL (wrong echo)")}, closed {socket.State}.");
        }
        catch (Exception error) { GD.Print($"Relay runtime check: WebSocket through an invoker: FAIL ({error.GetType().Name}: {error.Message})."); }
    }
}
