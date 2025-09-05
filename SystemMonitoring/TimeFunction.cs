using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public static class TimeFunction
{
    public static TimeZoneInfo GetTaipeiTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"); } // Windows
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); }      // Linux
            catch { return TimeZoneInfo.Local; }
        }
    }

    // 取得「現在之後」最近的整 N 分鐘時間點（依指定時區對齊）
    public static DateTimeOffset GetNextByInterval(DateTimeOffset nowUtc, int minutes, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);

        // 取當小時的 00 分，再加上下一個區塊（ex: 每 10 分 → 10、20、30…）
        int nextBlock = (local.Minute / minutes + 1) * minutes;

        var roundedHourLocal = new DateTimeOffset(
            local.Year, local.Month, local.Day,
            local.Hour, 0, 0,
            local.Offset
        );

        var candidateLocal = roundedHourLocal.AddMinutes(nextBlock);

        // 回傳 UTC 的 DateTimeOffset
        return candidateLocal.ToUniversalTime();
    }

    public static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b, DateTimeOffset c)
        => (a <= b && a <= c) ? a : (b <= a && b <= c) ? b : c;
}