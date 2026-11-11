using System;
using System.IO;
using CCLite.Services;

// 恢复键盘到当前档案的固件灯效（清除软件动效残留）
var logPath = @"C:\Users\ZZD\.zcode\workspace\draft-bb3c6a5c-fe35-4e6d-bb2b-29b39eae5088\cc-lite\ProtoTest\restore.log";
using var log = new StreamWriter(logPath, append: false);
void P(string s) { Console.WriteLine(s); log.WriteLine(s); }

P($"admin={Environment.IsPrivilegedProcess} wmi={GmWmiService.EnsureInit()}");

uint mode = PowerModeService.GetCurrentMode();
var (profile, moduleId) = LedService.ProfileForMode(mode);
var parts = LedService.LoadProfile(profile);
P($"mode={mode} profile={profile}");
for (int i = 0; i < 3; i++)
{
    var data = parts[2 + i];
    byte bri = LedService.GetBrightness(data);
    if (bri > 2) data = LedService.MakeData(LedService.GetMode(data), 2, LedService.GetColor(data));
    bool ok = LedService.WriteZone(LedService.KeyboardSlots[i], data);
    P($"  zone{i}: data=0x{data:X8} mode={LedService.GetMode(data)} bri={LedService.GetBrightness(data)} -> {ok}");
}
P("done");
