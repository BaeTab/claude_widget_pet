using System.Diagnostics;
using Claude_Widget.Services;

// ---------------------------------------------------------------------------
// IPC round-trip self-test (remote/log-friendly, no GUI eyeballing).
//
//   1. PetOverlayService binds a loopback listener + spawns Godot (console exe,
//      stdout/stderr captured).
//   2. Wait for the engine's `hello` handshake (Connected event).
//   3. Send {"type":"state","value":"working"} and confirm Godot logged that it
//      APPLIED it (its stderr line "[ipc] applied state=working ...").
//   4. Send an emote, then a clean shutdown; force-kill on lingering.
//
// Prints IPC_SELFTEST_PASS iff hello + state-apply both observed, else _FAIL.
// ---------------------------------------------------------------------------

string godot = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("GODOT4_CONSOLE_EXE") ?? "";
string project = args.Length > 1 ? args[1] : "";

if (godot.Length == 0 || project.Length == 0)
{
    Console.Error.WriteLine("usage: IpcSelfTest <godot_console_exe> <godot_overlay_dir>");
    return 2;
}

void L(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {m}");

bool helloSeen = false;
bool stateApplied = false;
bool clickSeen = false;
var helloTcs = new TaskCompletionSource();

var svc = new PetOverlayService(new PetOverlayService.Options
{
    GodotExePath = godot,
    ProjectPath = project,
    RedirectEngineOutput = true,
    Log = m =>
    {
        L(m);
        // Godot prints IPC diagnostics to stderr → "[godot!] ..." via the service.
        if (m.Contains("[ipc] applied state=") && m.Contains("working"))
            stateApplied = true;
    },
});

svc.Connected += () =>
{
    helloSeen = true;
    L("EVENT: Connected (hello received)");
    helloTcs.TrySetResult();
};
svc.CharacterClicked += () =>
{
    clickSeen = true;
    L("EVENT: CharacterClicked");
};
svc.EngineExited += reason => L($"EVENT: EngineExited ({reason})");

L("=== IPC self-test starting ===");
svc.Start();

// 1) handshake
var winner = await Task.WhenAny(helloTcs.Task, Task.Delay(TimeSpan.FromSeconds(25)));
if (winner != helloTcs.Task)
{
    L("TIMEOUT: no hello within 25s");
}
else
{
    // 2) command → engine applies it
    await svc.SendAsync(new { type = "state", value = "working", urgent = false });
    L("SENT: state=working");

    var sw = Stopwatch.StartNew();
    while (!stateApplied && sw.ElapsedMilliseconds < 8000)
        await Task.Delay(100);

    // 3) exercise an emote too (best-effort, not required for PASS)
    await svc.SendAsync(new { type = "emote", value = "celebrate" });
    L("SENT: emote=celebrate");
    await Task.Delay(1200);
}

// 4) clean shutdown
L("Shutting down engine...");
await svc.ShutdownAsync(2000);
svc.Dispose();
await Task.Delay(300);

bool pass = helloSeen && stateApplied;
L($"RESULT: hello={helloSeen} stateApplied={stateApplied} click={clickSeen}");
Console.WriteLine(pass ? "IPC_SELFTEST_PASS" : "IPC_SELFTEST_FAIL");
return pass ? 0 : 1;
