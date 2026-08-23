using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.Core.Interfaces;

public interface IMediaEngine : IDisposable
{
    MediaInfo? CurrentMedia { get; }

    /// <summary>現在の再生状態。表示側はこの値を唯一の情報源とし、独自に状態を持たないこと。</summary>
    PlaybackState State { get; }

    /// <summary>
    /// 停止処理でパイプラインのスレッドが止まりきらず、内部資源を解放できないまま取り残された状態。
    /// この間は <see cref="Play"/> が失敗する（ファイルを開き直すと解除される）。
    /// 表示側はこれを見て、無言で失敗させる代わりに復旧手段を案内すること。
    /// </summary>
    bool IsPipelineQuarantined { get; }

    /// <summary>
    /// 音声出力が異常停止した状態。この間は再生を再開しても再生位置と映像が進まない
    /// （ファイルを開き直すと解除される）。表示側はこれを見て、無言で失敗させる代わりに
    /// 復旧手段を案内すること。
    /// </summary>
    bool IsAudioOutputFailed { get; }

    /// <summary>
    /// 再生状態が変化したときに発火する。UI スレッド以外からも発火するため、
    /// 購読側でディスパッチャへ移すこと。
    /// </summary>
    event EventHandler<PlaybackState> StateChanged;

    TimeSpan Position { get; }
    double PlaybackSpeed { get; }

    void Open(string filePath);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetPlaybackSpeed(double speed);
    void StepForward();
    void StepBackward();
    void SetTrackVolume(int trackNumber, float volume);
    void SetTrackMute(int trackNumber, bool muted);
    void SetMasterVolume(float volume);

    IReadOnlyList<ChapterInfo> GetChapters();
    void JumpToChapter(int index);
    void JumpToPreviousChapter();
    void JumpToNextChapter();

    /// <summary>
    /// 現在位置に表示すべき新しいフレームがあればリースして返す（無ければ null）。
    /// 呼び出し側は使い終えたら必ず ReturnFrame で返却すること。呼び出しは UI スレッドから行う想定。
    /// </summary>
    VideoFrameLease? TryGetFrame(TimeSpan position);
    void ReturnFrame(VideoFrameLease lease);

    event EventHandler<TimeSpan> PositionChanged;
    event EventHandler PlaybackEnded;
    event EventHandler<PlaybackStatistics>? StatisticsUpdated;

    /// <summary>
    /// 再生を継続できない異常（音声出力の停止など）が起きたことを知らせる。
    /// UI スレッド以外からも発火するため、購読側でディスパッチャへ移すこと。
    /// 引数はそのままユーザーへ提示できる文面。
    /// </summary>
    event EventHandler<string>? PlaybackFailed;

    /// <summary>
    /// 映像フレームリングを新規に構築したとき。共有テクスチャのハンドルが総入れ替えになるため、
    /// ハンドルをキーにキャッシュしている描画側はここで破棄・再取得すること。
    /// ファイル切替だけでなく、同じファイルの停止→再生でもパイプラインごと作り直されるため発火する。
    /// </summary>
    event EventHandler? VideoRingRebuilt;
}

public record PlaybackStatistics(int DroppedFrames, int DisplayedFrames, double VideoLagSec);
