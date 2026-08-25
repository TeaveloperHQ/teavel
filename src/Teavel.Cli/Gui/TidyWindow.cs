using System.Drawing;
using System.Windows.Forms;
using Teavel.M365;

namespace Teavel.Cli.Gui;

/// <summary>정리 후보 하나를 어떻게 할지.</summary>
public enum TidyAction
{
    /// <summary>손대지 않는다. 잘 모르겠으면 이것이다.</summary>
    Keep,

    /// <summary>이름만 바꾼다. 안의 파일·대화는 그대로 남는다.</summary>
    Rename,

    /// <summary>이름 앞에 연도를 붙이고 학생만 내보낸다. 팀과 자료는 그대로 둔다.</summary>
    Archive,

    /// <summary>지운다. 되돌릴 수 없다.</summary>
    Delete,
}

/// <summary>창에 올릴 후보 하나.</summary>
/// <param name="Item">분류 결과.</param>
/// <param name="ArchiveName">보관하면 붙일 이름. 만든 연도를 모르면 빈 문자열이고, 그러면 보관을 못 고른다.</param>
public sealed record TidyRow(TriagedGroup Item, string ArchiveName);

/// <summary>관리자가 정한 것 하나.</summary>
public sealed record TidyDecision(ExistingGroup Group, TidyAction Action, string NewName);

/// <summary>
/// <b>⑤ 정리</b> — 지난 몇 년치가 어질러진 그룹 목록을 한 화면에 놓고 정한다.
///
/// <para>
/// 콘솔에서는 후보 하나마다 [1][2][3][4] 를 묻고 다음으로 넘어갔다. 서른 개면 서른 번이고,
/// 열 번째에서 앞의 판단을 바꾸고 싶어도 되돌아갈 수 없었다. 정리는 <b>견주어 보는 일</b>이라
/// — 이건 시험용 잔재고 저건 진짜 수업 그룹이고 — 목록이 한눈에 보여야 판단이 선다.
/// </para>
/// <para>
/// 그래도 <b>기본값은 '그냥 두기' 다.</b> 아무것도 안 하고 [적용]을 눌러도 아무 일도
/// 일어나지 않는다. 지우기는 파일과 대화가 함께 사라지는 일이라 한 번 더 막아 둔다
/// (<see cref="DeleteGate"/>).
/// </para>
/// </summary>
public sealed class TidyWindow : Form
{
    private const string Keep = "그냥 두기";
    private const string Rename = "이름 바꾸기";
    private const string Archive = "지난 학년도로 보관";
    private const string Delete = "지우기";

    private const int ColName = 0;
    private const int ColWhat = 1;
    private const int ColNote = 2;
    private const int ColDo = 3;
    private const int ColNew = 4;

    private readonly IReadOnlyList<TidyRow> _rows;
    private readonly DataGridView _grid = Desk.Grid();
    private readonly Label _summary = new() { Font = Desk.Body, ForeColor = Desk.Faint, AutoSize = true };
    private readonly Button _apply = Desk.Button("적용합니다", primary: true);

    public IReadOnlyList<TidyDecision> Result { get; private set; } = Array.Empty<TidyDecision>();

    /// <summary>창을 띄운다. 닫거나 취소하면 <c>null</c> — 그때는 콘솔로 하나씩 묻는다.</summary>
    public static IReadOnlyList<TidyDecision>? Open(IReadOnlyList<TidyRow> rows)
        => Desk.Run(() => new TidyWindow(rows), f => f.Result);

    private TidyWindow(IReadOnlyList<TidyRow> rows)
    {
        _rows = rows;

        Desk.Dress(this, "정리", 1060, 620);

        var cancel = Desk.Button("취소");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _apply.Click += (_, _) => Apply();
        CancelButton = cancel;

        BuildGrid();
        Fill();
        Retally();

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 12), BackColor = Desk.Paper };
        pad.Controls.Add(_grid);

        Controls.Add(pad);
        Controls.Add(Desk.Footer(_summary, _apply, cancel));
        Controls.Add(Desk.Header(
            "정리해 볼 만한 것",
            "지우면 그 안의 파일과 대화가 함께 사라집니다. 이름만 바꾸면 내용은 그대로 남습니다. "
          + "잘 모르겠으면 '그냥 두기' 로 두세요."));
    }

    private void BuildGrid()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "이름", Width = 240, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "무엇인지", Width = 190, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "왜 후보인지",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 160,
            ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "할 일",
            Width = 170,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "새 이름", Width = 210 });

        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColDo) return;
            Follow(e.RowIndex);
            Retally();
        };

        // 목록에서 고른 것이 곧바로 반영돼야 아래 요약과 '새 이름' 칸이 따라온다.
        // 이것이 없으면 다른 줄로 옮겨야 그때 반영된다.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.ColumnIndex == ColDo)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == ColNew) Retally();
        };
    }

    private void Fill()
    {
        foreach (var r in _rows)
        {
            var g = r.Item.Group;
            var i = _grid.Rows.Add();
            var row = _grid.Rows[i];
            row.Tag = r;

            row.Cells[ColName].Value = g.DisplayName;
            row.Cells[ColName].Style.Font = Desk.Strong;
            row.Cells[ColWhat].Value = What(g);
            row.Cells[ColNote].Value = r.Item.Note;

            Desk.Quiet(row.Cells[ColWhat]);
            Desk.Quiet(row.Cells[ColNote]);

            var pick = (DataGridViewComboBoxCell)row.Cells[ColDo];
            pick.Items.Add(Keep);
            pick.Items.Add(Rename);
            if (r.ArchiveName.Length > 0) pick.Items.Add(Archive);
            pick.Items.Add(Delete);
            pick.Value = Keep;

            Follow(i);
        }
    }

    private static string What(ExistingGroup g)
    {
        var parts = new List<string> { g.IsTeam ? "팀" : "그룹" };
        parts.Add(g.MemberCount >= 0 ? $"구성원 {g.MemberCount}명" : "구성원 모름");
        if (g.Created.Length > 0) parts.Add($"{g.Created} 에 만듦");
        return string.Join(" · ", parts);
    }

    /// <summary>고른 것에 맞춰 '새 이름' 칸과 줄 색을 맞춘다.</summary>
    private void Follow(int index)
    {
        var row = _grid.Rows[index];
        var r = (TidyRow)row.Tag!;
        var pick = row.Cells[ColDo].Value as string ?? Keep;
        var name = row.Cells[ColNew];

        switch (pick)
        {
            case Rename:
                name.ReadOnly = false;
                name.Style.BackColor = Desk.Paper;
                name.Style.ForeColor = Desk.Ink;
                name.Style.SelectionBackColor = Color.FromArgb(232, 240, 250);
                name.Style.SelectionForeColor = Desk.Ink;
                // 지금 이름을 넣어 둔다. 대개 한두 글자만 고치면 되는데 빈칸이면 처음부터 쳐야 한다.
                if (name.Value is not string s || s.Length == 0) name.Value = r.Item.Group.DisplayName;
                row.Cells[ColName].Style.ForeColor = Desk.Ink;
                break;

            case Archive:
                name.Value = r.ArchiveName;
                Desk.Quiet(name);
                row.Cells[ColName].Style.ForeColor = Desk.Accent;
                break;

            case Delete:
                name.Value = "";
                Desk.Quiet(name);
                row.Cells[ColName].Style.ForeColor = Desk.Danger;
                break;

            default:
                name.Value = "";
                Desk.Quiet(name);
                row.Cells[ColName].Style.ForeColor = Desk.Ink;
                break;
        }
    }

    private void Retally()
    {
        var (rename, archive, delete) = Tally();
        var parts = new List<string>();
        if (rename > 0) parts.Add($"이름 바꿀 것 {rename}개");
        if (archive > 0) parts.Add($"보관할 것 {archive}개");
        if (delete > 0) parts.Add($"지울 것 {delete}개");

        _summary.Text = parts.Count == 0
            ? $"후보 {_rows.Count}개 — 아직 아무것도 정하지 않으셨습니다. 이대로 적용하면 아무 일도 일어나지 않습니다."
            : string.Join("  ·  ", parts);
        _summary.ForeColor = delete > 0 ? Desk.Danger : Desk.Faint;
        _apply.Text = parts.Count == 0 ? "이대로 넘어갑니다" : "적용합니다";
    }

    private (int Rename, int Archive, int Delete) Tally()
    {
        int rename = 0, archive = 0, delete = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            switch (row.Cells[ColDo].Value as string)
            {
                case Rename: rename++; break;
                case Archive: archive++; break;
                case Delete: delete++; break;
            }
        }
        return (rename, archive, delete);
    }

    private void Apply()
    {
        _grid.EndEdit();

        var decisions = new List<TidyDecision>();
        var doomed = new List<ExistingGroup>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var r = (TidyRow)row.Tag!;
            var pick = row.Cells[ColDo].Value as string ?? Keep;
            var typed = (row.Cells[ColNew].Value as string ?? "").Trim();

            switch (pick)
            {
                case Rename:
                    if (!NameOk(r, typed)) return;
                    if (string.Equals(typed, r.Item.Group.DisplayName, StringComparison.Ordinal)) continue;
                    decisions.Add(new TidyDecision(r.Item.Group, TidyAction.Rename, typed));
                    break;

                case Archive:
                    decisions.Add(new TidyDecision(r.Item.Group, TidyAction.Archive, r.ArchiveName));
                    break;

                case Delete:
                    decisions.Add(new TidyDecision(r.Item.Group, TidyAction.Delete, ""));
                    doomed.Add(r.Item.Group);
                    break;
            }
        }

        // 지우기는 여기서 한 번 더 막는다. 잘못 고른 채로 [적용]을 누르는 일은 반드시 생긴다.
        if (doomed.Count > 0 && !DeleteGate.Passed(this, doomed)) return;

        Result = decisions;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// 새 이름이 이름 같은지.
    /// </summary>
    /// <remarks>
    /// 창에 파일을 끌어다 놓거나 다른 곳에서 복사한 것을 그대로 붙여넣는 일이 있는데,
    /// 그대로 두면 학교 그룹 이름이 파일 경로가 된다. 콘솔 쪽에서 실제로 그렇게 됐다.
    /// </remarks>
    private bool NameOk(TidyRow r, string typed)
    {
        string? wrong = typed.Length == 0 ? "새 이름이 비어 있습니다."
            : typed.Length > 60 ? "이름이 너무 깁니다(60자까지)."
            : typed.IndexOfAny(new[] { '\\', '/' }) >= 0 ? "이름에 \\ 나 / 는 쓸 수 없습니다. 파일 경로를 붙여넣으신 것 같습니다."
            : null;

        if (wrong is null) return true;

        MessageBox.Show(this,
            $"'{r.Item.Group.DisplayName}' 의 새 이름을 다시 봐 주세요.\n\n{wrong}",
            "새 이름", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
}

/// <summary>
/// 지우기 앞에 두는 문.
/// </summary>
/// <remarks>
/// <para>
/// 콘솔에서는 <b>그룹 이름을 그대로 받아 적게</b> 했다. Enter 한 번에 지워지는 일을 막으려는
/// 것인데, 창에서 열 개를 받아 적게 하면 아무도 안 한다.
/// </para>
/// <para>
/// 그래서 뜻은 같게 두고 방식만 바꿨다 — <b>지울 것마다 하나씩 표시해야</b> 단추가 열린다.
/// 무엇이 사라지는지 이름으로 다시 읽게 하는 것이 이 문의 목적이고, 그것은 그대로다.
/// </para>
/// </remarks>
internal sealed class DeleteGate : Form
{
    public static bool Passed(IWin32Window owner, IReadOnlyList<ExistingGroup> doomed)
    {
        using var gate = new DeleteGate(doomed);
        return gate.ShowDialog(owner) == DialogResult.OK;
    }

    private DeleteGate(IReadOnlyList<ExistingGroup> doomed)
    {
        Text = "Teavel — 지우기 전에";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 150 + Math.Min(doomed.Count, 9) * 30);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Desk.Paper;
        Font = Desk.Body;
        ShowIcon = false;

        var go = Desk.Button("지웁니다", primary: true);
        go.Enabled = false;
        go.ForeColor = Desk.Danger;

        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            Font = Desk.Body,
            BorderStyle = BorderStyle.None,
            CheckOnClick = true,
            IntegralHeight = false,
            ItemHeight = 28,
        };

        foreach (var g in doomed)
        {
            var what = g.MemberCount >= 0 ? $"구성원 {g.MemberCount}명" : "구성원 모름";
            list.Items.Add($"{g.DisplayName}   ({(g.IsTeam ? "팀" : "그룹")} · {what})");
        }

        // ItemCheck 는 바뀌기 '전' 에 온다. 지금 값 대신 e.NewValue 로 세어야 한 칸씩 밀리지 않는다.
        list.ItemCheck += (_, e) =>
        {
            var ticked = list.CheckedIndices.Count
                       + (e.NewValue == CheckState.Checked ? 1 : 0)
                       - (list.GetItemChecked(e.Index) ? 1 : 0);
            go.Enabled = ticked == doomed.Count;
        };

        var cancel = Desk.Button("그만둡니다");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        go.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        CancelButton = cancel;

        var summary = new Label
        {
            Text = $"{doomed.Count}개를 지웁니다. 하나씩 확인해 주세요.",
            Font = Desk.Body,
            ForeColor = Desk.Danger,
            AutoSize = true,
        };

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8), BackColor = Desk.Paper };
        pad.Controls.Add(list);

        Controls.Add(pad);
        Controls.Add(Desk.Footer(summary, go, cancel));
        Controls.Add(Desk.Header(
            "되돌릴 수 없습니다",
            "지우면 그 안의 파일과 대화가 함께 사라집니다. 되살릴 방법이 없습니다."));
    }
}
