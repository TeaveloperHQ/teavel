using System.Drawing;
using System.Windows.Forms;

namespace Teavel.Cli.Gui;

/// <summary>
/// 창을 여는 자리.
///
/// <para>
/// Teavel 은 PowerShell 창에서 쓰는 콘솔 앱이고 그것은 그대로다. 창은 <b>목록을 놓고
/// 골라야 하는 자리</b>에서만 열린다 — 정리 후보 서른 개, 반 스무 개의 담임, 반별 학생.
/// 콘솔에서 이런 것을 다루면 한 줄씩 순서대로 묻게 되고, 되돌릴 수가 없다.
/// </para>
/// <para>
/// <b>창은 판단만 받아 오고 실행하지 않는다.</b> 그룹을 지우고 사람을 넣는 일은 창이
/// 닫힌 뒤에 흐름이 한다. 이 규칙 덕분에 상주 PowerShell 이 흘려보내는 진행 문구가
/// 창 뒤에 가려지지 않고, 무엇을 했는지가 콘솔에 그대로 남는다.
/// </para>
/// </summary>
public static class Desk
{
    /// <summary>이 자리에서 창을 열어도 되는지.</summary>
    /// <remarks>
    /// <para>
    /// 넷 다 맞아야 연다. 하나라도 아니면 콘솔로 간다 — 창을 못 여는 것이
    /// 기능을 못 쓰는 것이 되어서는 안 된다.
    /// </para>
    /// <list type="bullet">
    /// <item>Windows 여야 한다. WinForms 는 다른 곳에서 돌지 않는다.</item>
    /// <item><b>입력이 파이프로 들어오면 안 된다.</b> 가짜 테넌트 시험은 답을 흘려 넣는데,
    ///       그때 창이 뜨면 아무도 없는 화면 앞에서 멈춘다.</item>
    /// <item><c>TEAVEL_NO_GUI</c> 가 없어야 한다. 원격 접속처럼 창이 곤란한 자리를 위한 구멍.</item>
    /// </list>
    /// </remarks>
    public static bool Available { get; } =
        OperatingSystem.IsWindows()
        && Environment.UserInteractive
        && !Console.IsInputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAVEL_NO_GUI"));

    /// <summary>맑은 고딕. 없으면 WinForms 가 알아서 다른 것으로 갈음한다.</summary>
    public static Font Body { get; } = new("맑은 고딕", 10.5f);
    public static Font Strong { get; } = new("맑은 고딕", 10.5f, FontStyle.Bold);
    public static Font Head { get; } = new("맑은 고딕", 15f, FontStyle.Bold);
    public static Font Small { get; } = new("맑은 고딕", 9f);

    public static Color Ink { get; } = Color.FromArgb(28, 28, 30);
    public static Color Faint { get; } = Color.FromArgb(120, 120, 128);
    public static Color Line { get; } = Color.FromArgb(222, 222, 228);
    public static Color Paper { get; } = Color.White;
    public static Color Band { get; } = Color.FromArgb(247, 247, 250);
    public static Color Accent { get; } = Color.FromArgb(0, 90, 158);
    public static Color Danger { get; } = Color.FromArgb(178, 34, 34);

    /// <summary>
    /// 창 하나를 띄우고 결과를 받아 온다. 닫거나 취소하면 <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <b>창은 반드시 제 STA 스레드에서 만들어야 한다.</b> 이 프로그램의 Main 은
    /// 최상위 문(top-level statements)이라 STA 가 아니고, 거기서 만든 컨트롤을
    /// 다른 스레드에서 보이면 클립보드·드래그 같은 것이 조용히 어긋난다.
    /// 그래서 만드는 것부터 결과를 꺼내는 것까지 전부 이 안에서 한다.
    /// </remarks>
    public static TResult? Run<TForm, TResult>(Func<TForm> make, Func<TForm, TResult> take)
        where TForm : Form
        where TResult : class
    {
        TResult? result = null;
        Exception? blew = null;

        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using var form = make();
                if (form.ShowDialog() == DialogResult.OK) result = take(form);
            }
            catch (Exception ex) { blew = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // 창이 터졌다고 흐름까지 끝내지 않는다. 콘솔 길이 그대로 있다.
        if (blew is not null)
        {
            Ui.Warn("창을 여는 데 실패했습니다. 콘솔로 진행합니다.");
            Ui.Dim($"      {blew.Message}");
        }

        return result;
    }

    /// <summary>콘솔에서 창으로 넘어갈 때 남기는 안내. 창이 다른 화면에 뜨는 일이 있다.</summary>
    public static void Handoff()
    {
        Console.WriteLine();
        Ui.Info("창을 열었습니다. 창에서 정하시면 여기로 돌아옵니다.");
        Ui.Dim("      창이 안 보이면 작업 표시줄을 확인해 주세요.");
    }

    /// <summary>창 맨 위의 제목 자리.</summary>
    public static Panel Header(string title, string subtitle)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Paper };

        var t = new Label { Text = title, Font = Head, ForeColor = Ink, AutoSize = true, Location = new Point(20, 14) };
        var s = new Label { Text = subtitle, Font = Small, ForeColor = Faint, AutoSize = true, Location = new Point(22, 48) };
        var rule = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Line };

        panel.Controls.Add(t);
        panel.Controls.Add(s);
        panel.Controls.Add(rule);
        return panel;
    }

    /// <summary>아래쪽 단추 줄. 왼쪽에 한 줄 요약, 오른쪽에 단추.</summary>
    public static Panel Footer(Control summary, params Control[] buttons)
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Band, Padding = new Padding(20, 13, 20, 13) };
        var rule = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Line };

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            BackColor = Band,
        };
        foreach (var b in buttons) right.Controls.Add(b);

        summary.Dock = DockStyle.Left;
        summary.AutoSize = true;
        summary.Padding = new Padding(0, 8, 0, 0);

        panel.Controls.Add(summary);
        panel.Controls.Add(right);
        panel.Controls.Add(rule);
        return panel;
    }

    public static Button Button(string text, bool primary = false)
        => new()
        {
            Text = text,
            Font = primary ? Strong : Body,
            AutoSize = false,
            Size = new Size(primary ? 150 : 100, 38),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.System,
            UseVisualStyleBackColor = true,
        };

    /// <summary>표. 학교 목록은 길어서 읽는 맛이 곧 쓸모다.</summary>
    public static DataGridView Grid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Paper,
            BorderStyle = BorderStyle.None,
            Font = Body,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EditMode = DataGridViewEditMode.EditOnEnter,
            GridColor = Line,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        };

        g.ColumnHeadersDefaultCellStyle.Font = Strong;
        g.ColumnHeadersDefaultCellStyle.BackColor = Band;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Faint;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Band;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        g.ColumnHeadersHeight = 34;
        g.EnableHeadersVisualStyles = false;

        g.DefaultCellStyle.ForeColor = Ink;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 240, 250);
        g.DefaultCellStyle.SelectionForeColor = Ink;
        g.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        g.RowTemplate.Height = 36;

        // 목록 칸을 한 번 눌러 바로 펼 수 있게. 두 번 눌러야 열리면 아무도 못 찾는다.
        g.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (g.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn or DataGridViewCheckBoxColumn)
                g.BeginEdit(true);
        };

        // 목록에 없는 값이 들어오면 WinForms 가 빨간 창을 띄운다. 그것을 보여 줄 이유가 없다.
        g.DataError += (_, e) => { e.ThrowException = false; };

        return g;
    }

    /// <summary>읽기 전용 칸. 회색으로 눕혀 둔다 — 눌러도 안 되는 것은 그렇게 보여야 한다.</summary>
    public static void Quiet(DataGridViewCell cell)
    {
        cell.ReadOnly = true;
        cell.Style.ForeColor = Faint;
        cell.Style.BackColor = Band;
        cell.Style.SelectionBackColor = Band;
        cell.Style.SelectionForeColor = Faint;
    }

    /// <summary>공통 창 모양. 콘솔에서 띄우면 뒤에 숨는 일이 있어 앞으로 끌어온다.</summary>
    public static void Dress(Form form, string title, int width, int height)
    {
        form.Text = "Teavel — " + title;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ClientSize = new Size(width, height);
        form.MinimumSize = new Size(820, 500);
        form.BackColor = Paper;
        form.Font = Body;
        form.ShowIcon = false;
        form.MinimizeBox = false;
        form.Shown += (_, _) => { form.Activate(); form.BringToFront(); };
    }
}
