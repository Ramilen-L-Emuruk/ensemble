using System.Collections.Specialized;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.UI.ViewModels;

namespace MultiTrackPlayer.UI.Controls;

/// <summary>
/// チャプターの変更を <see cref="SeekBarControl"/> のマーカーへ反映する配線。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeekBarControl.SetChapters"/> は命令型 API でバインドを持たないため、以前は
/// 変更しうる経路（ファイルを開く 4 経路・次/前送り 4 箇所・プレイリストのダブルクリック・
/// 自動送り・チャプター一覧の 4 経路）から手で呼んでいて、半分以上が漏れていた。
/// フルスクリーン側のシークバーへは呼び出しが 1 つも無く、マーカーは常に空だった。
/// <see cref="MainViewModel.Chapters"/> は安定インスタンスで変更は必ず
/// <see cref="MainViewModel.RefreshChapters"/> を通るので、ここで拾えば経路が増えても漏れない。
/// </para>
/// <para>
/// 通常時とフルスクリーンで 2 つのシークバーがあり、両方が同じ配線を必要とする。
/// 片方だけ直して他方を放置する事故（`.claude/rules/ensemble-review.md` §2）を避けるため、
/// 配線はこのクラスに 1 つだけ置く。
/// </para>
/// </remarks>
internal sealed class SeekBarChapterSync
{
    private readonly SeekBarControl _target;
    private MainViewModel? _source;
    private bool _updateScheduled;

    public SeekBarChapterSync(SeekBarControl target) => _target = target;

    /// <summary>監視する ViewModel を設定・差し替える。null を渡すと購読を解除する。</summary>
    public void Bind(MainViewModel? source)
    {
        if (ReferenceEquals(_source, source)) return;

        if (_source != null) _source.Chapters.CollectionChanged -= OnChaptersChanged;
        _source = source;
        if (_source != null) _source.Chapters.CollectionChanged += OnChaptersChanged;
        ScheduleUpdate();
    }

    private void OnChaptersChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleUpdate();

    /// <summary>
    /// マーカーの描き直しを予約する。<see cref="MainViewModel.RefreshChapters"/> は
    /// <c>Clear()</c> の後に 1 件ずつ <c>Add()</c> するため通知は件数 +1 回来る。
    /// そのたびに描き直すのは無駄なので 1 回にまとめる。
    /// </summary>
    private void ScheduleUpdate()
    {
        if (_updateScheduled) return;
        _updateScheduled = true;
        _target.Dispatcher.BeginInvoke(() =>
        {
            _updateScheduled = false;
            _target.SetChapters(_source?.BuildChapterMarkers() ?? Array.Empty<ChapterMarker>());
        });
    }
}
