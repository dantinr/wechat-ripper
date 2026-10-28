namespace WeChatNotes;

internal class RangeCheckedListBox : CheckedListBox
{
    private int anchor = -1;
    private bool anchorChecked, rangeMouseUp;
    internal bool UpdatingChecks { get; private set; }
    internal event Action? ChecksUpdated;

    internal void ResetRange() { anchor = -1; rangeMouseUp = false; }
    private void CheckRange(int first, int last, bool value)
    {
        UpdatingChecks = true; BeginUpdate();
        try
        {
            for (int i = Math.Min(first, last); i <= Math.Max(first, last); i++) SetItemChecked(i, value);
        }
        finally { EndUpdate(); UpdatingChecks = false; ChecksUpdated?.Invoke(); }
    }
    internal void CheckAll(bool value)
    {
        ResetRange();
        if (Items.Count > 0) CheckRange(0, Items.Count - 1, value);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x0201 or 0x0203) // WM_LBUTTONDOWN / WM_LBUTTONDBLCLK
        {
            long point = m.LParam.ToInt64();
            int index = IndexFromPoint(unchecked((short)point), unchecked((short)(point >> 16)));
            if (index >= 0)
            {
                if ((m.WParam.ToInt64() & 4) != 0 && anchor >= 0 && anchor < Items.Count)
                {
                    Focus(); SelectedIndex = index;
                    CheckRange(anchor, index, anchorChecked);
                    rangeMouseUp = true; m.Result = 0; return;
                }
                rangeMouseUp = false;
                base.WndProc(ref m);
                anchor = index; anchorChecked = GetItemChecked(index);
                return;
            }
        }
        else if (m.Msg == 0x0202)
        {
            if (rangeMouseUp) // Native checkbox logic must not toggle the endpoint again.
            {
                rangeMouseUp = false; m.Result = 0; return;
            }
            base.WndProc(ref m);
            // The native list commits its checkbox change on mouse-up.
            if (anchor >= 0 && SelectedIndex >= 0)
            {
                anchor = SelectedIndex; anchorChecked = GetItemChecked(anchor);
            }
            return;
        }
        base.WndProc(ref m);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.A)
        {
            CheckAll(!e.Shift); e.Handled = e.SuppressKeyPress = true; return;
        }
        if (e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown) ResetRange();
        base.OnKeyDown(e);
    }
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (e.KeyChar == ' ' && SelectedIndex >= 0) { anchor = SelectedIndex; anchorChecked = GetItemChecked(anchor); }
    }
}
