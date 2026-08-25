using System.Drawing;
using System.Windows.Forms;
using Teavel.M365;
using Teavel.Roster;

namespace Teavel.Cli.Gui;

/// <summary>한 반에 실제로 넣기로 한 사람들.</summary>
public sealed record MemberPick(string ClassKey, ExistingGroup Team, IReadOnlyList<RosterRow> People);

/// <summary>
/// <b>⑦ 학생 넣기</b> — 어느 반 학생을 어느 팀에 넣을지 보고 고른다.
///
/// <para>
/// 콘솔에서는 반별 요약을 죽 찍고 <b>"{n}명을 넣을까요?" 한 번</b>을 물었다. 전부 넣거나
/// 전부 안 넣거나 둘뿐이라, 한 반만 빼고 싶거나 전학 간 아이 하나를 빼고 싶으면 방법이 없었다.
/// 명단 파일을 고쳐서 다시 돌리는 수밖에 없었는데, 그건 관리자가 할 일이 아니다.
/// </para>
/// <para>
/// 그래서 이 창은 <b>반 단위와 사람 단위 두 겹</b>으로 고르게 한다. 왼쪽에서 반을 끄고,
/// 오른쪽에서 그 반의 학생 하나를 끈다. 기본은 전부 켜져 있다 — 명단대로 넣는 것이
/// 하려던 일이고, 창은 예외를 다루는 자리다.
/// </para>
/// <para>
/// <b>이미 들어 있는 사람은 애초에 목록에 없다.</b> 그건 창이 아니라
/// <see cref="MemberPlanner"/> 가 이미 빼 두었다 — 여러 번 돌려도 안전해야 하기 때문이다.
/// </para>
/// </summary>
public sealed class MemberWindow : Form
{
    private const int ColTick = 0;
    private const int ColClass = 1;
    private const int ColTeam = 2;
    private const int ColCount = 3;
    private const int ColNote = 4;

    private readonly IReadOnlyList<ClassAssignment> _plan;

    /// <summary>반마다 <b>빼기로 한</b> 사람들. 기본은 비어 있다 — 명단대로 넣는 것이 기본이다.</summary>
    private readonly Dictionary<string, HashSet<string>> _dropped = new(StringComparer.OrdinalIgnoreCase);

    private readonly DataGridView _grid = Desk.Grid();
    private readonly CheckedListBox _people = new();
    private readonly Label _who = new();
    private readonly Label _summary = new() { Font = Desk.Body, ForeColor = Desk.Faint, AutoSize = true };
    private readonly Button _apply = Desk.Button("넣습니다", primary: true);

    private ClassAssignment? _showing;
    private bool _filling;

    public IReadOnlyList<MemberPick> Result { get; private set; } = Array.Empty<MemberPick>();

    /// <summary>창을 띄운다. 닫거나 취소하면 <c>null</c> — 그때는 콘솔로 한 번에 묻는다.</summary>
    public static IReadOnlyList<MemberPick>? Open(IReadOnlyList<ClassAssignment> plan)
        => Desk.Run(() => new MemberWindow(plan), f => f.Result);

    private MemberWindow(IReadOnlyList<ClassAssignment> plan)
    {
        _plan = plan;
        foreach (var a in plan) _dropped[a.ClassKey] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Desk.Dress(this, "학생 넣기", 1120, 660);

        var cancel = Desk.Button("취소");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _apply.Click += (_, _) => Apply();
        CancelButton = cancel;

        BuildGrid();
        Fill();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 12,
            BackColor = Desk.Paper,
        };
        split.Panel1.BackColor = Desk.Paper;
        split.Panel2.BackColor = Desk.Paper;
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(BuildPeople());

        // 가르는 자리와 최소 폭은 <b>크기가 잡힌 뒤에, 이 순서로</b> 넣어야 한다.
        //
        // 만들면서 넣으면 그때 이것의 폭은 아직 150 이라 '최소 420' 이 들어가는 순간
        // 터진다 — 창이 아예 안 뜬다. 자리를 먼저 옮기고 최소 폭을 나중에 올리는 것도
        // 같은 이유다. 거꾸로 하면 최소 폭이 지금 자리보다 커서 또 터진다.
        //
        // 한 번만 하고 손을 뗀다. 매번 다시 넣으면 관리자가 끌어 옮길 수가 없다.
        void Place(object? s, EventArgs e)
        {
            var w = split.Width;
            if (w < 560) return;

            split.SplitterDistance = w - 400;

            if (w >= 800)
            {
                split.Panel1MinSize = 360;
                split.Panel2MinSize = 260;
            }

            split.SizeChanged -= Place;
        }
        split.SizeChanged += Place;

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 12), BackColor = Desk.Paper };
        pad.Controls.Add(split);

        Controls.Add(pad);
        Controls.Add(Desk.Footer(_summary, _apply, cancel));
        Controls.Add(Desk.Header(
            "명단의 학생을 반별 팀에 넣습니다",
            "이미 들어 있는 사람은 빼고 셌습니다. 반을 고르면 오른쪽에 그 반 학생이 나옵니다."));

        ShowClass(_plan.FirstOrDefault(a => a.CanApply) ?? _plan.FirstOrDefault());
        Retally();
    }

    private void BuildGrid()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 46 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "반", Width = 120, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "팀",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 150,
            ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "넣을 사람", Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "그 밖에", Width = 190, ReadOnly = true });

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.ColumnIndex == ColTick)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColTick) return;
            Recolor(_grid.Rows[e.RowIndex]);
            Retally();
        };

        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.CurrentRow?.Tag is ClassAssignment a) ShowClass(a);
        };
    }

    private Control BuildPeople()
    {
        var box = new Panel { Dock = DockStyle.Fill, BackColor = Desk.Paper };

        _who.Dock = DockStyle.Top;
        _who.Height = 30;
        _who.Font = Desk.Strong;
        _who.ForeColor = Desk.Ink;
        _who.Padding = new Padding(4, 6, 0, 0);

        _people.Dock = DockStyle.Fill;
        _people.Font = Desk.Body;
        _people.BorderStyle = BorderStyle.FixedSingle;
        _people.CheckOnClick = true;
        _people.IntegralHeight = false;
        _people.ItemHeight = 26;

        _people.ItemCheck += (_, e) =>
        {
            if (_filling || _showing is null) return;

            var person = _showing.ToAdd[e.Index];
            var drop = _dropped[_showing.ClassKey];

            if (e.NewValue == CheckState.Checked) drop.Remove(person.Upn);
            else drop.Add(person.Upn);

            // 사람을 다 빼면 그 반은 넣을 것이 없다. 왼쪽 표시도 함께 내린다 —
            // 왼쪽은 켜져 있는데 넣을 사람이 0명인 상태는 읽는 사람을 헷갈리게 한다.
            var row = RowOf(_showing);
            if (row is not null && drop.Count == _showing.ToAdd.Count)
                row.Cells[ColTick].Value = false;
            else if (row is not null && _showing.ToAdd.Count > 0)
                row.Cells[ColTick].Value = true;

            BeginInvoke(Retally);
        };

        var all = Desk.Button("모두 넣기");
        var none = Desk.Button("모두 빼기");
        all.Click += (_, _) => Sweep(true);
        none.Click += (_, _) => Sweep(false);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Desk.Paper,
            Padding = new Padding(0, 6, 0, 0),
        };
        bar.Controls.Add(all);
        bar.Controls.Add(none);

        box.Controls.Add(_people);
        box.Controls.Add(bar);
        box.Controls.Add(_who);
        return box;
    }

    private void Fill()
    {
        foreach (var a in _plan)
        {
            var i = _grid.Rows.Add();
            var row = _grid.Rows[i];
            row.Tag = a;

            row.Cells[ColClass].Value = a.ClassKey;
            row.Cells[ColClass].Style.Font = Desk.Strong;
            row.Cells[ColTeam].Value = a.Team?.DisplayName ?? "—";
            row.Cells[ColCount].Value = a.ToAdd.Count > 0 ? $"{a.ToAdd.Count}명" : "—";

            row.Cells[ColNote].Value = a.Problem.Length > 0 ? a.Problem
                : a.Already > 0 ? $"이미 {a.Already}명 들어 있음"
                : "";

            Desk.Quiet(row.Cells[ColTeam]);
            Desk.Quiet(row.Cells[ColCount]);
            Desk.Quiet(row.Cells[ColNote]);

            if (a.Problem.Length > 0) row.Cells[ColNote].Style.ForeColor = Desk.Danger;

            // 넣을 수 없는 반은 켜지지 않는다. 켤 수 있게 두면 켜 놓고 아무 일도 안 일어난다.
            if (!a.CanApply)
            {
                row.Cells[ColTick].Value = false;
                Desk.Quiet(row.Cells[ColTick]);
                row.Cells[ColClass].Style.ForeColor = Desk.Faint;
            }
            else
            {
                row.Cells[ColTick].Value = true;
            }
        }
    }

    private DataGridViewRow? RowOf(ClassAssignment a)
        => _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ReferenceEquals(r.Tag, a));

    private void ShowClass(ClassAssignment? a)
    {
        if (ReferenceEquals(a, _showing)) return;
        _showing = a;

        _filling = true;
        _people.Items.Clear();

        if (a is null)
        {
            _who.Text = "";
            _filling = false;
            return;
        }

        _who.Text = a.ToAdd.Count > 0
            ? $"{a.ClassKey} — 넣을 학생 {a.ToAdd.Count}명"
            : $"{a.ClassKey} — 넣을 학생이 없습니다";

        var drop = _dropped[a.ClassKey];
        foreach (var p in a.ToAdd)
        {
            var number = p.Number.Length > 0 ? $"{p.Number}번 " : "";
            _people.Items.Add($"{number}{p.Name}   {p.Upn}", !drop.Contains(p.Upn));
        }

        _people.Enabled = a.CanApply;
        _filling = false;
    }

    private void Sweep(bool keep)
    {
        if (_showing is null || !_showing.CanApply) return;

        var drop = _dropped[_showing.ClassKey];
        drop.Clear();
        if (!keep) foreach (var p in _showing.ToAdd) drop.Add(p.Upn);

        _filling = true;
        for (var i = 0; i < _people.Items.Count; i++) _people.SetItemChecked(i, keep);
        _filling = false;

        var row = RowOf(_showing);
        if (row is not null) row.Cells[ColTick].Value = keep;

        Retally();
    }

    private void Recolor(DataGridViewRow row)
    {
        var on = row.Cells[ColTick].Value is true;
        var a = (ClassAssignment)row.Tag!;
        row.Cells[ColClass].Style.ForeColor = !a.CanApply ? Desk.Faint : on ? Desk.Ink : Desk.Faint;
    }

    /// <summary>지금 넣기로 돼 있는 것.</summary>
    private List<MemberPick> Picks()
    {
        var picks = new List<MemberPick>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var a = (ClassAssignment)row.Tag!;
            if (!a.CanApply || row.Cells[ColTick].Value is not true) continue;

            var drop = _dropped[a.ClassKey];
            var people = a.ToAdd.Where(p => !drop.Contains(p.Upn)).ToList();
            if (people.Count == 0) continue;

            picks.Add(new MemberPick(a.ClassKey, a.Team!, people));
        }

        return picks;
    }

    private void Retally()
    {
        var picks = Picks();
        var total = picks.Sum(p => p.People.Count);
        var possible = _plan.Where(a => a.CanApply).Sum(a => a.ToAdd.Count);
        var stuck = _plan.Count(a => a.Problem.Length > 0);

        var parts = new List<string>();
        parts.Add(total == 0 ? "넣을 사람을 고르지 않으셨습니다" : $"{picks.Count}개 반 · {total}명을 넣습니다");
        if (total < possible) parts.Add($"{possible - total}명은 뺐습니다");
        if (stuck > 0) parts.Add($"넣을 수 없는 반 {stuck}개");

        _summary.Text = string.Join("  ·  ", parts);
        _apply.Text = total == 0 ? "넣지 않고 넘어갑니다" : $"{total}명을 넣습니다";
    }

    private void Apply()
    {
        _grid.EndEdit();
        Result = Picks();
        DialogResult = DialogResult.OK;
        Close();
    }
}
