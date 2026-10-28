namespace WeChatNotes;

internal static class ConversationRows
{
    // Scrolling shifts the row grid independently of the window. Infer that shift
    // from repeated horizontal separators, including gaps around the selected row.
    // Require several agreeing separators so message text cannot establish the grid.
    internal static Rectangle[] Locate(Bitmap image, FocusCalibration calibration)
    {
        if (!calibration.Valid || image.Size != new Size(calibration.WindowWidth, calibration.WindowHeight)) return [];
        int left = calibration.X + (int)(calibration.Height * 1.2), right = calibration.X + calibration.Width - 13;
        if (right - left < 60) return [];
        var separators = new List<int>();
        static int Light(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
        for (int y = calibration.Y + 1; y < image.Height - 11; y++)
        {
            int[] line = new int[9], above = new int[9], below = new int[9];
            for (int i = 0; i < line.Length; i++)
            {
                int x = left + (right - left) * i / (line.Length - 1);
                line[i] = Light(image.GetPixel(x, y)); above[i] = Light(image.GetPixel(x, y - 1)); below[i] = Light(image.GetPixel(x, y + 1));
            }
            int middle = line.Order().ElementAt(4);
            // A hand-drawn calibration may put one edge sample just outside the divider.
            if (line.Count(value => Math.Abs(value - middle) <= 3) < 8) continue;
            int up = above.Order().ElementAt(4) - middle, down = below.Order().ElementAt(4) - middle;
            if (Math.Abs(up) is >= 3 and <= 35 && Math.Abs(down) is >= 3 and <= 35 && Math.Sign(up) == Math.Sign(down)) separators.Add(y + 1);
        }
        int height = calibration.Height;
        var heights = separators.Zip(separators.Skip(1), (first, second) => second - first)
            .Where(distance => Math.Abs(distance - calibration.Height) <= Math.Max(4, calibration.Height / 10))
            .GroupBy(distance => distance).OrderByDescending(group => group.Count()).ToArray();
        if (heights.Length > 0 && heights[0].Count() >= 3 && (heights.Length == 1 || heights[0].Count() > heights[1].Count())) height = heights[0].Key;
        int Phase(int top) => (top - calibration.Y) % height;
        int Distance(int a, int b) { int distance = Math.Abs(a - b); return Math.Min(distance, height - distance); }
        var votes = separators.Select(top => (Phase: Phase(top), Count: separators.Count(other => Distance(Phase(top), Phase(other)) <= 2)))
            .OrderByDescending(v => v.Count).ToArray();
        if (votes.Length == 0 || votes[0].Count < 3 || votes[0].Count * 3 < separators.Count * 2 ||
            votes.Any(v => v.Count == votes[0].Count && Distance(v.Phase, votes[0].Phase) > 2)) return [];
        int phase = votes[0].Phase;
        var rows = new List<Rectangle>();
        for (int top = calibration.Y + phase; top + height < image.Height - 10; top += height)
            rows.Add(new(calibration.X, top, calibration.Width, height));
        return rows.ToArray();
    }
}
