using System;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>自动分配游戏内存的计算。</summary>
public static class GameMemoryAuto
{
    // 堆过大会拉长 GC 停顿，自动分配不超过该值
    public const double MaxGb = 12;

    /// modCount 为 null 表示实例不可装 Mod
    public static double Calculate(double availableGb, int? modCount, bool hasOptiFine)
    {
        // 确定需求的内存值
        double ramMininum; // 无论如何也需要保证的最低限度内存
        double ramTarget1; // 估计能勉强带动了的内存
        double ramTarget2; // 估计没啥问题了的内存
        double ramTarget3; // 放一百万个材质和 Mod 和光影需要的内存
        if (modCount is not null)
        {
            // 可安装 Mod 的实例
            ramMininum = 0.5d + modCount.Value / 150d;
            ramTarget1 = 1.5d + modCount.Value / 90d;
            ramTarget2 = 2.7d + modCount.Value / 50d;
            ramTarget3 = 4.5d + modCount.Value / 25d;
        }
        else if (hasOptiFine)
        {
            // OptiFine 实例
            ramMininum = 0.5d;
            ramTarget1 = 1.5d;
            ramTarget2 = 3d;
            ramTarget3 = 5d;
        }
        else
        {
            // 普通实例
            ramMininum = 0.5d;
            ramTarget1 = 1.5d;
            ramTarget2 = 2.5d;
            ramTarget3 = 4d;
        }

        var ramGive = 0d;
        var ramAvailable = availableGb;
        var ramStages = new[]
        {
            (Delta: ramTarget1, Ratio: 1d),
            (Delta: ramTarget2 - ramTarget1, Ratio: 0.7d),
            (Delta: ramTarget3 - ramTarget2, Ratio: 0.4d),
            (Delta: ramTarget3, Ratio: 0.15d)
        };
        foreach (var (RamDelta, RamRatio) in ramStages)
        {
            ramGive += Math.Min(ramAvailable * RamRatio, RamDelta);
            ramAvailable -= RamDelta / RamRatio;
            if (ramAvailable < 0.1d)
                break;
        }

        // 不低于最低值
        var result = Math.Round(Math.Max(ramGive, ramMininum), 1);
        return Math.Min(result, MaxGb);
    }
}
