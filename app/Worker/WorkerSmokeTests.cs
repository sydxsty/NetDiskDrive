using System.Text.Json;

namespace OverlayDisk.Worker;

internal static class WorkerSmokeTests
{
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        if (UnelevatedLauncher.IsAdministrator) throw new InvalidOperationException("Worker smoke must begin in a standard-user process.");
        var checks = new List<string>();
        await using var client = new PrivilegedWorkerClient();
        try
        {
            if (client.IsConnected) throw new Exception("Worker started without a privileged action.");
            checks.Add("constructing client does not elevate");
            JsonElement empty = JsonSerializer.SerializeToElement(new { });
            object? state = await client.InvokeAsync("state", empty, default);
            if (!client.IsConnected || state is not JsonElement { ValueKind: JsonValueKind.Object }) throw new Exception("State request did not reach administrator worker.");
            int? processId = client.WorkerProcessId;
            checks.Add("standard user to administrator authenticated state RPC");
            int lost = 0; client.ConnectionLost += (_, _) => lost++;
            client.DisconnectForSmokeTest();
            if (client.IsConnected || lost != 1) throw new Exception("Disconnect status/event was not delivered.");
            await client.InvokeAsync("state", empty, default);
            if (!client.IsConnected || client.WorkerProcessId != processId) throw new Exception("Reconnect started a duplicate worker.");
            checks.Add("disconnect reconnects original process with original session token");
            await client.ShutdownAsync();
            if (client.IsConnected) throw new Exception("Shutdown left client connected.");
            checks.Add("safe worker shutdown without disk operations");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, elevated = false, checks }));
            Console.WriteLine("WORKER_SMOKE_OK"); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString(), checks }));
            throw;
        }
    }
}
