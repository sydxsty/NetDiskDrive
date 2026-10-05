using System.Buffers;
using System.Text;
using System.Text.Json;
using OverlayDisk.Services;

namespace OverlayDisk;

/// <summary>Pure buffer/settings regressions; does not open a native volume or user settings.</summary>
internal static class ManagedIoSelfTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var pool = new ReusingPool();
        var first = NativeJsonBuffer.Read(bytes => Fill(bytes, "{\"name\":\"first\",\"nested\":{\"value\":37}}"), 512, pool);
        Check(pool.Cleared && first.GetProperty("nested").GetProperty("value").GetInt32() == 37, "returned JSON owns its nested content");
        var second = NativeJsonBuffer.Read(bytes => Fill(bytes, "{\"name\":\"second\"}"), 512, pool);
        Check(first.GetProperty("name").GetString() == "first" && second.GetProperty("name").GetString() == "second",
            "reusing and clearing the native buffer changed an earlier result");
        int returned = pool.Returns;
        bool failed = false;
        try
        {
            NativeJsonBuffer.Read(bytes => { Fill(bytes, "fixture-private-error-output"); throw new IOException("injected native failure"); }, 512, pool);
        }
        catch (IOException) { failed = true; }
        Check(failed && pool.Returns == returned + 1 && pool.Cleared, "native failure did not clear and return its buffer");
        failed = false;
        try { NativeJsonBuffer.Read(bytes => Fill(bytes, "{broken}"), 512, pool); }
        catch (JsonException) { failed = true; }
        Check(failed && pool.Cleared, "malformed native JSON did not return cleared storage");
        failed = false;
        try { NativeJsonBuffer.Read(bytes => Array.Fill(bytes, (byte)'x'), 512, pool); }
        catch (IOException) { failed = true; }
        Check(failed && pool.Cleared && pool.Rents == pool.Returns, "unterminated native output retained a pooled buffer");

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            NativeJsonBuffer.Read(bytes => Fill(bytes, JsonSerializer.Serialize(new { index = i, text = new string((char)('a' + i), 100000) }))))));
        for (int i = 0; i < concurrent.Length; i++)
            Check(concurrent[i].GetProperty("index").GetInt32() == i && concurrent[i].GetProperty("text").GetString() == new string((char)('a' + i), 100000),
                "parallel large JSON results shared temporary storage");

        var oldSettings = JsonSerializer.Deserialize<AppSettings>("{}", SettingsStorage.Json)!;
        Check(oldSettings.SyncOnExit, "legacy settings must retain the safe existing exit default");
        var optedOut = new AppSettings { SyncOnExit = false, Prefetch = new("adaptive", 16), DefaultObjectSizeBytes = 16 << 20 }; string pending = Guid.NewGuid().ToString(); optedOut.PendingDisks.Add(pending);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(optedOut, SettingsStorage.Json), SettingsStorage.Json)!;
        Check(!reloaded.SyncOnExit && reloaded.Prefetch == optedOut.Prefetch && reloaded.DefaultObjectSizeBytes == optedOut.DefaultObjectSizeBytes && reloaded.PendingDisks.SetEquals([pending]), "exit preference or unsynced marker was lost during settings serialization");
        return [
            "Pooled native JSON is detached before reuse, isolated across concurrent large responses, and cleared on native/parse/termination failures.",
            "Missing SyncOnExit keeps the prior true default; explicit false and persistent pending disk markers survive settings serialization."
        ];
    }
    private static void Fill(byte[] bytes, string json)
    {
        int written = Encoding.UTF8.GetBytes(json, bytes); bytes[written] = 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new IOException("Managed I/O fixture: " + message); }
    private sealed class ReusingPool : ArrayPool<byte>
    {
        private byte[] buffer = Array.Empty<byte>();
        private bool rented;
        internal int Rents, Returns;
        internal bool Cleared => !rented && buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0;
        public override byte[] Rent(int minimumLength)
        {
            Check(!rented, "fixture double rent"); rented = true; Rents++;
            if (buffer.Length < minimumLength) buffer = new byte[minimumLength];
            return buffer;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            Check(rented && ReferenceEquals(array, buffer) && clearArray, "native output returned to the wrong pool or without clearing");
            Array.Clear(array); rented = false; Returns++;
        }
    }
}
