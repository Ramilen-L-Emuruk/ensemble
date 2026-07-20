namespace MultiTrackPlayer.Engine.Video;

/// <summary>リング内の Ready スロット1件。Pts 昇順（=デコード順=表示順）で渡される前提。</summary>
public readonly record struct CandidateFrame(int SlotIndex, double Pts);

public readonly struct FrameSelection
{
    public int? SlotIndex { get; }
    public int DroppedCount { get; }

    private FrameSelection(int? slotIndex, int droppedCount)
    {
        SlotIndex = slotIndex;
        DroppedCount = droppedCount;
    }

    public static readonly FrameSelection None = new(null, 0);
    public static FrameSelection Selected(int slotIndex, int droppedCount) => new(slotIndex, droppedCount);
}

/// <summary>
/// クロック位置に対して「表示すべき最新の due フレーム」を選び、それより古い Ready フレームを
/// drop 数として計上する。VideoFrameRing のネイティブメモリには依存しない純ロジック。
/// </summary>
public static class FrameSelector
{
    public static FrameSelection SelectDue(
        IReadOnlyList<CandidateFrame> candidates,
        double clockPositionSeconds,
        double frameDurationSeconds)
    {
        if (candidates.Count == 0) return FrameSelection.None;

        double dueThreshold = clockPositionSeconds + frameDurationSeconds / 2.0;

        int lastDueIndex = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Pts <= dueThreshold) lastDueIndex = i;
            else break;
        }

        if (lastDueIndex < 0) return FrameSelection.None;

        int dropped = lastDueIndex;
        return FrameSelection.Selected(candidates[lastDueIndex].SlotIndex, dropped);
    }
}
