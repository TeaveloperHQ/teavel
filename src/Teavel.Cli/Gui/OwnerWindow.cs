using System.Drawing;
using System.Windows.Forms;
using Teavel.M365;

namespace Teavel.Cli.Gui;

/// <summary>창에 올릴 반 하나.</summary>
/// <param name="ClassKey">'1학년 3반'.</param>
/// <param name="TeamName">그 반의 팀 이름. 학교마다 표기가 달라 함께 보여 준다.</param>
/// <param name="GroupId">팀 id.</param>
/// <param name="CurrentOwner">이미 있는 소유자. 없으면 빈 문자열.</param>
public sealed record OwnerRow(string ClassKey, string TeamName, string GroupId, string CurrentOwner);

/// <summary>관리자가 정한 담임 하나.</summary>
public sealed record OwnerPick(string ClassKey, string GroupId, TenantUser Teacher);

/// <summary>
/// <b>⑧ 담임 선생님</b> — 반마다 팀 소유자를 정한다.
///
/// <para>
/// 이 창이 생긴 이유가 가장 분명한 자리다. 콘솔에서는 반 스무 개면 <b>스무 번을 순서대로</b>
/// 물었다. 앞에서 적은 것을 고칠 수 없었고, 지금 몇 개나 정했는지도 보이지 않았고,
/// 동명이인이 나오면 그 자리에서 번호를 골라야 다음으로 갔다.
/// </para>
/// <para>
/// 창에서는 <b>고를 것이 이미 목록에 있다.</b> 성을 한 글자 치면 그 성의 선생님들이 좁혀지고,
/// 아이디가 함께 보여 동명이인이 갈린다. 모르는 반은 그냥 비워 두면 된다 — 나중에 다시
/// 실행하면 그 반만 다시 묻는다.
/// </para>
/// <para>
/// <b>이미 소유자가 있는 반은 눕혀 둔다.</b> 콘솔에서는 아예 보여 주지 않았는데,
/// 그러면 관리자가 전체 그림을 못 본다 — 스무 반 중 몇 반이 이미 됐는지가 안 보이면
/// 남은 여덟 개가 전부인 줄 안다.
/// </para>
/// </summary>
public sealed class OwnerWindow : Form
{
    private const int ColClass = 0;
    private const int ColTeam = 1;
    private const int ColNow = 2;
    private const int ColPick = 3;

    private readonly IReadOnlyList<OwnerRow> _rows;
    private readonly Dictionary<string, TenantUser> _byLabel = new(StringComparer.Ordinal);
    private readonly DataGridView _grid = Desk.Grid();
    private readonly Label _summary = new() { Font = Desk.Body, ForeColor = Desk.Faint, AutoSize = true };
    private readonly Button _apply = Desk.Button("이대로 정합니다", primary: true);

    public IReadOnlyList<OwnerPick> Result { get; private set; } = Array.Empty<OwnerPick>();

    /// <summary>창을 띄운다. 닫거나 취소하면 <c>null</c> — 그때는 콘솔로 하나씩 묻는다.</summary>
    public static IReadOnlyList<OwnerPick>? Open(
        IReadOnlyList<OwnerRow> rows, IReadOnlyList<TenantUser> teachers)
        => Desk.Run(() => new OwnerWindow(rows, teachers), f => f.Result);

    private OwnerWindow(IReadOnlyList<OwnerRow> rows, IReadOnlyList<TenantUser> teachers)
    {
        _rows = rows;

        foreach (var t in teachers)
        {
            // 아이디를 함께 적는다. 동명이인이 학교엔 흔하고, 이름만으로는 갈리지 않는다.
            var label = $"{t.DisplayName}   {t.Upn}";
            _byLabel[label] = t;
        }

        Desk.Dress(this, "담임 선생님", 1020, 640);

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
            "반마다 담임 선생님",
            "담임을 정해 두면 그 선생님이 팀 설정을 바꾸고 과제를 낼 수 있습니다. "
          + "성함 앞 글자를 치면 좁혀집니다. 모르는 반은 비워 두세요."));
    }

    private void BuildGrid()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "반", Width = 130, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "팀 이름", Width = 220, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "지금", Width = 150, ReadOnly = true });

        var pick = new DataGridViewComboBoxColumn
        {
            HeaderText = "담임 선생님",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 260,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        };
        pick.Items.Add("");
        foreach (var label in _byLabel.Keys.OrderBy(k => k, StringComparer.CurrentCulture)) pick.Items.Add(label);
        _grid.Columns.Add(pick);

        // 목록만 있고 칠 수 없으면 선생님 이백 명 중에서 손으로 찾게 된다.
        // 편집 컨트롤은 칸마다 새로 오지 않고 <b>돌려 쓴다</b> — 그래서 열릴 때마다 다시 맞춘다.
        _grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is not ComboBox box) return;
            box.DropDownStyle = ComboBoxStyle.DropDown;
            box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            box.AutoCompleteSource = AutoCompleteSource.ListItems;
            box.Font = Desk.Body;
        };

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.ColumnIndex == ColPick)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColPick) return;
            var cell = _grid.Rows[e.RowIndex].Cells[ColPick];
            cell.Style.ForeColor = (cell.Value as string) is { Length: > 0 } ? Desk.Accent : Desk.Ink;
            Retally();
        };
    }

    private void Fill()
    {
        foreach (var r in _rows)
        {
            var i = _grid.Rows.Add();
            var row = _grid.Rows[i];
            row.Tag = r;

            row.Cells[ColClass].Value = r.ClassKey;
            row.Cells[ColClass].Style.Font = Desk.Strong;
            row.Cells[ColTeam].Value = r.TeamName;
            Desk.Quiet(row.Cells[ColTeam]);

            var taken = r.CurrentOwner.Length > 0;
            row.Cells[ColNow].Value = taken ? r.CurrentOwner : "없음";
            Desk.Quiet(row.Cells[ColNow]);
            if (!taken) row.Cells[ColNow].Style.ForeColor = Desk.Danger;

            // 이미 소유자가 있는 반은 건드리지 않는다. 보이기는 해야 전체 그림이 보인다.
            if (taken)
            {
                row.Cells[ColPick].Value = "";
                Desk.Quiet(row.Cells[ColPick]);
                row.Cells[ColClass].Style.ForeColor = Desk.Faint;
            }
            else
            {
                row.Cells[ColPick].Value = "";
            }
        }
    }

    private void Retally()
    {
        var open = _rows.Count(r => r.CurrentOwner.Length == 0);
        var done = Picks().Count;

        _summary.Text = open == 0
            ? "모든 반에 담임 선생님이 이미 있습니다."
            : $"담임 없는 반 {open}개 중 {done}개를 정하셨습니다."
              + (done < open ? "  ·  남은 반은 나중에 다시 실행하시면 됩니다." : "");

        _apply.Text = done == 0 ? "정하지 않고 넘어갑니다" : $"{done}개 반의 담임을 정합니다";
    }

    private List<OwnerPick> Picks()
    {
        var picks = new List<OwnerPick>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var r = (OwnerRow)row.Tag!;
            if (r.CurrentOwner.Length > 0) continue;

            var label = (row.Cells[ColPick].Value as string ?? "").Trim();
            if (label.Length == 0) continue;
            if (!_byLabel.TryGetValue(label, out var who)) continue;

            picks.Add(new OwnerPick(r.ClassKey, r.GroupId, who));
        }

        return picks;
    }

    private void Apply()
    {
        _grid.EndEdit();
        var picks = Picks();

        // 한 분을 두 반의 담임으로 앉히는 것은 대개 잘못 고른 것이다. 막지는 않는다 —
        // 작은 학교에서는 정말 그럴 수 있다. 다만 그냥 지나가게 두지도 않는다.
        var twice = picks.GroupBy(p => p.Teacher.Upn, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1)
                         .ToList();

        if (twice.Count > 0)
        {
            var lines = twice.Select(g =>
                $"  · {g.First().Teacher.DisplayName} — {string.Join(", ", g.Select(x => x.ClassKey))}");

            var answer = MessageBox.Show(this,
                "같은 선생님이 두 반 이상의 담임으로 지정돼 있습니다.\n\n"
                + string.Join("\n", lines)
                + "\n\n이대로 진행할까요?",
                "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;
        }

        Result = picks;
        DialogResult = DialogResult.OK;
        Close();
    }
}
