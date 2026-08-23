using System.Globalization;
using System.Windows.Data;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.UI;

/// <summary>
/// プレイリストの項目が「プレイヤーに読み込まれているファイル」かどうかを、再生状態に応じた
/// 記号で表す。該当しない項目には何も出さない（空文字）。
/// </summary>
/// <remarks>
/// 判定の元は <see cref="MediaInfo.FilePath"/>（MediaEngine.Open に渡した文字列がそのまま入る）で、
/// プレイリストの項目と同じ文字列になる。
/// 一覧の選択位置と PlaylistCursor.Path は使わない。前者はユーザーが自由に動かせるので
/// 読み込んでいるファイルを表さず、後者は開けなかったファイルも指すため
/// （開けなかったファイルに「再生中」が付く）。
/// 引数は順に「項目のパス」「読み込み済みのメディア情報」「再生状態」。
/// </remarks>
[ValueConversion(typeof(object), typeof(string))]
public class LoadedFileGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // テンプレートの初期化中は UnsetValue が渡ることがあるため、型で受け止める
        if (values.Length < 3 || values[0] is not string path || values[1] is not MediaInfo media)
            return string.Empty;
        if (!string.Equals(path, media.FilePath, StringComparison.Ordinal))
            return string.Empty;
        return values[2] is PlaybackState state ? Glyph(state) : string.Empty;
    }

    /// <summary><see cref="PlaybackState"/> の全 3 値に対応させる。値を増やしたらここも追うこと。
    /// 再生を終えた後はファイルを読み込んだまま <see cref="PlaybackState.Stopped"/> になるので、
    /// 停止も「読み込んでいる」印として表す。</summary>
    private static string Glyph(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "▶",
        PlaybackState.Paused => "⏸",
        PlaybackState.Stopped => "■",
        _ => string.Empty
    };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
