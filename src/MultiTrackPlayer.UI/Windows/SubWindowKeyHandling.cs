using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MultiTrackPlayer.UI.Windows;

/// <summary>
/// サブウィンドウ（ミキサー・プレイリスト・チャプター・デバッグ・ショートカット一覧）共通のキー入力処理。
/// ESCキーでウィンドウを閉じ、それ以外のキーはメインウィンドウのショートカットへ転送する。
/// </summary>
internal static class SubWindowKeyHandling
{
    public static void AttachEscapeAndShortcutForwarding(Window window)
    {
        window.KeyDown += (_, e) =>
        {
            // テキスト編集中（例: チャプタータイトルの編集ボックス）はキー入力を文字として
            // 扱うべきで、キーバインドと一致した文字でコマンドが誤爆してはいけない。
            // Escape の判定より先に置く。後ろに置くと、テキスト入力を持つサブウィンドウを
            // 増やしたときに「編集のキャンセル」が「ウィンドウを閉じる」に化ける
            //（今は ChapterWindow の TextBox が自前で Escape を Handled にしているだけで、
            //  この順序に守られていたわけではない）
            if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;

            if (e.Key == Key.Escape)
            {
                window.Hide();
                e.Handled = true;
                return;
            }

            if (window.Owner is MainWindow mainWindow)
                mainWindow.HandleShortcutKey(e);
        };
    }
}
