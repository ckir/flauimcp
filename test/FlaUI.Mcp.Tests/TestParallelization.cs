using Xunit;

// These tests create UIA3Automation (COM/STA) and open real desktop windows.
// xUnit runs test classes in parallel by default; concurrent UIA clients plus
// many windows opening/closing on the shared desktop cause transient COM
// HRESULT failures (GetDesktop) and window-launch races (LaunchApp timeouts).
// Serialize the whole assembly so UIA work never overlaps.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
