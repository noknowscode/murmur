using SkiaSharp;

namespace Murmuration;

/// <summary>The point a flock orbits: a centre, an orbit amplitude and an angular speed.</summary>
public struct Attractor
{
    public float Cx, Cy, Ax, Ay, W;
    public Attractor(float cx, float cy, float ax, float ay, float w) { Cx = cx; Cy = cy; Ax = ax; Ay = ay; W = w; }
}

/// <summary>
/// One flock of boids in host coordinates. Each bird steers by up to seven neighbours
/// (separate, align, cohere) plus a roaming attractor, and can be handed a perch to land on.
/// All mutation happens on the UI thread under <see cref="FlockHost.Gate"/>; drawing takes the same lock.
/// </summary>
public sealed class Flock : IDisposable
{
    public const byte Gone = 0, Flying = 1, Assigned = 2;

    const float R = 30f, R2 = R * R, S2 = 100f, AlignK = 5f, CohK = 7f, SepForce = 2400f;
    static readonly int[] OffX = { 0, -1, 1, 0, 0, -1, 1, -1, 1 };
    static readonly int[] OffY = { 0, 0, 0, -1, 1, -1, -1, 1, 1 };
    static readonly float[] StrokeByDepth = { 1.15f, 1.5f, 1.9f };
    const int MaxColors = 8, DepthLevels = 3, AlphaLevels = 5;

    public readonly int N;
    public readonly float[] X, Y, Z, VX, VY, VZ, TX, TY, Rel, Home, Alpha;
    public readonly byte[] Mode, Sc, Tc, Team;
    public readonly bool[] Launched;
    public readonly short[] Grp;
    public readonly List<SKColor> Palette = new();

    public readonly Attractor[] A = new Attractor[2];
    public readonly Attractor[] Goal = new Attractor[2];

    /// <summary>Cruise speed band, px/s.</summary>
    public float VMin = 200, VMax = 360;
    public float Pull = 6, SepK = 1;
    /// <summary>Landing: blend into homing, then delay and duration of the final settle.</summary>
    public float HRamp = 0.25f, HsDelay = 0.35f, HsDur = 0.4f, ArrK = 6, ArrMax = 560;
    public float T, Fade = 1, GatherStart;
    public bool InkOnLaunch, DrawWaiting, Perch;
    /// <summary>Set by the host while the owning control is not visible.</summary>
    public bool Hidden;
    public byte MixColor;
    /// <summary>Optional vertical offset for landed birds, used for the completion wave.</summary>
    public Func<int, float, float>? Hop;
    /// <summary>Soft walls, host coordinates.</summary>
    public float Left, Top, Right = 400, Bottom = 400;

    readonly int[] _next, _cell;
    int[] _head = Array.Empty<int>();
    float _p; bool _split;
    readonly float _ph;
    readonly Random _rng = new();
    readonly SKPathBuilder?[] _paths = new SKPathBuilder?[MaxColors * DepthLevels * AlphaLevels];
    readonly bool[] _used = new bool[MaxColors * DepthLevels * AlphaLevels];
    readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

    public Flock(int n)
    {
        N = n;
        X = new float[n]; Y = new float[n]; Z = new float[n]; VX = new float[n]; VY = new float[n]; VZ = new float[n];
        TX = new float[n]; TY = new float[n]; Rel = new float[n]; Home = new float[n]; Alpha = new float[n];
        Mode = new byte[n]; Sc = new byte[n]; Tc = new byte[n]; Team = new byte[n]; Launched = new bool[n]; Grp = new short[n];
        _next = new int[n]; _cell = new int[n];
        Array.Fill(Home, float.PositiveInfinity); Array.Fill(Alpha, 1f); Array.Fill(Mode, Flying); Array.Fill(Grp, (short)-1);
        _ph = (float)(_rng.NextDouble() * Math.PI * 2);
    }

    float Rand() => (float)_rng.NextDouble();
    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>Returns the palette slot for a colour, adding it if it is new.</summary>
    public byte ColorIndex(SKColor c)
    {
        for (int k = 0; k < Palette.Count; k++)
        {
            var p = Palette[k];
            if (Math.Abs(p.Red - c.Red) + Math.Abs(p.Green - c.Green) + Math.Abs(p.Blue - c.Blue) < 28) return (byte)k;
        }
        if (Palette.Count >= MaxColors) return 0;
        Palette.Add(c.WithAlpha(255));
        return (byte)(Palette.Count - 1);
    }

    public void Aim(Attractor g) { Goal[0] = g; Goal[1] = g; }
    public void Aim(int k, Attractor g) => Goal[k] = g;
    public void Snap() { A[0] = Goal[0]; A[1] = Goal[1]; }
    public void SetWalls(float left, float top, float right, float bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }

    /// <summary>Moves every bird, perch and attractor, for when the owning control moves in the host.</summary>
    public void Translate(float dx, float dy)
    {
        for (int i = 0; i < N; i++) { X[i] += dx; Y[i] += dy; TX[i] += dx; TY[i] += dy; }
        for (int k = 0; k < 2; k++) { A[k].Cx += dx; A[k].Cy += dy; Goal[k].Cx += dx; Goal[k].Cy += dy; }
        Left += dx; Right += dx; Top += dy; Bottom += dy;
    }

    /// <summary>Birds start as the marks they replace. <paramref name="relOf"/> gives each one its lift-off delay in seconds.</summary>
    public void SpawnFromInk(InkPoints pts, Func<float, float, float> relOf)
    {
        for (int i = 0; i < N; i++)
        {
            int j = pts.Count == 0 ? 0 : i % pts.Count;
            if (pts.Count == 0) { X[i] = (Left + Right) / 2; Y[i] = Top; Sc[i] = 0; }
            else { X[i] = pts.X[j]; Y[i] = pts.Y[j]; Sc[i] = pts.C[j]; }
            Rel[i] = T + relOf(X[i], Y[i]); Launched[i] = false;
        }
        DrawWaiting = true; GatherStart = T;
    }

    /// <summary>Birds drift in along a horizontal line on a bell-shaped release, so the flock swells in.</summary>
    public void SpawnFromSky(float x0, float x1, float y, float span = 1.2f)
    {
        for (int i = 0; i < N; i++)
        {
            X[i] = x0 + Rand() * (x1 - x0); Y[i] = y - Rand() * 10; Sc[i] = 0;
            Rel[i] = T + span * (Rand() + Rand()) / 2; Launched[i] = false;
        }
        DrawWaiting = false; GatherStart = T;
    }

    public void Kick(float amount = 320)
    {
        for (int i = 0; i < N; i++) if (Mode[i] == Flying) { VX[i] += (Rand() - 0.5f) * amount; VY[i] += (Rand() - 0.5f) * amount; }
    }

    /// <summary>Divides the flock into a left and a right team. Each team follows its own attractor and ignores the other.</summary>
    public void Split()
    {
        var xs = (float[])X.Clone(); Array.Sort(xs); float median = xs[N / 2];
        for (int i = 0; i < N; i++) Team[i] = (byte)(X[i] < median ? 0 : 1);
        _split = true;
    }

    public void Merge() { Array.Clear(Team); _split = false; }

    public int CountFlying() { int c = 0; for (int i = 0; i < N; i++) if (Mode[i] == Flying) c++; return c; }

    /// <summary>
    /// Hands a set of perches to the nearest flying birds, paired in reading order, and returns how many were taken.
    /// </summary>
    public int Land(InkPoints pts, short grp, float lineK, float xK, float jitter)
    {
        var fly = new List<int>(N);
        for (int i = 0; i < N; i++) if (Mode[i] == Flying) fly.Add(i);
        int n = Math.Min(pts.Count, fly.Count);
        if (n == 0) return 0;
        float cx = pts.X0 + pts.W / 2, cy = pts.Y0 + pts.H / 2;
        var d = new float[N];
        foreach (int i in fly) { float dx = X[i] - cx, dy = Y[i] - cy; d[i] = dx * dx + dy * dy; }
        fly.Sort((a, b) => d[a].CompareTo(d[b]));
        var birds = fly.GetRange(0, n).ToArray();
        var spots = new int[n];
        for (int k = 0; k < n; k++) spots[k] = k;
        Order(birds, X, Y); Order(spots, pts.X, pts.Y);
        for (int k = 0; k < n; k++)
        {
            int i = birds[k], j = spots[k];
            TX[i] = pts.X[j]; TY[i] = pts.Y[j]; Tc[i] = pts.C[j]; Grp[i] = grp; Mode[i] = Assigned;
            float line = MathF.Min(2, MathF.Max(0, MathF.Floor((pts.Y[j] - pts.Y0) / pts.LineH)));
            Home[i] = T + 0.02f + line * lineK + xK * Clamp01((pts.X[j] - pts.X0) / pts.W) + Rand() * jitter;
        }
        return n;
    }

    // Sort along x, then within ten strips along y: a cheap pairing that keeps paths from crossing.
    static void Order(int[] ids, float[] xs, float[] ys)
    {
        Array.Sort(ids, (a, b) => xs[a].CompareTo(xs[b]));
        int s = Math.Max(1, (int)Math.Ceiling(ids.Length / 10.0));
        var byY = Comparer<int>.Create((a, b) => ys[a].CompareTo(ys[b]));
        for (int k = 0; k < ids.Length; k += s) Array.Sort(ids, k, Math.Min(s, ids.Length - k), byY);
    }

    /// <summary>Ends a landing group: birds either rejoin the flock or leave the simulation.</summary>
    public void Release(short grp, bool recycle)
    {
        for (int i = 0; i < N; i++)
        {
            if (Grp[i] != grp) continue;
            if (!recycle) { Mode[i] = Gone; continue; }
            Mode[i] = Flying; Grp[i] = -1; Home[i] = float.PositiveInfinity; Alpha[i] = 1;
            VX[i] = (Rand() - 0.5f) * 160; VY[i] = 90 + Rand() * 90; VZ[i] = (Rand() - 0.5f) * 80;
        }
    }

    public void SetGroupAlpha(short grp, float a) { for (int i = 0; i < N; i++) if (Grp[i] == grp) Alpha[i] = a; }

    public void Step(float dt)
    {
        T += dt;
        float e = 1 - MathF.Exp(-dt * 4);
        for (int k = 0; k < 2; k++)
        {
            A[k].Cx += (Goal[k].Cx - A[k].Cx) * e; A[k].Cy += (Goal[k].Cy - A[k].Cy) * e;
            A[k].Ax += (Goal[k].Ax - A[k].Ax) * e; A[k].Ay += (Goal[k].Ay - A[k].Ay) * e; A[k].W += (Goal[k].W - A[k].W) * e;
        }
        _p += A[0].W * dt;
        Span<float> atx = stackalloc float[2], aty = stackalloc float[2];
        for (int k = 0; k < 2; k++) { atx[k] = A[k].Cx + MathF.Sin(_p + _ph) * A[k].Ax; aty[k] = A[k].Cy + MathF.Sin(_p * 1.45f + _ph * 1.7f) * A[k].Ay; }
        float atz = MathF.Sin(_p * 0.8f + _ph) * 60;

        // Loose at lift-off: the pull and cohesion ease in, so the flock gathers instead of starting as a clump.
        float gq = Clamp01((T - GatherStart - 0.25f) / 1.3f), gk = 0.2f + 0.8f * gq * gq * (3 - 2 * gq);
        float pk = Pull * gk, ck = CohK * gk, sk = SepForce * SepK;

        int gc = Math.Max(1, (int)MathF.Ceiling((Right - Left) / R)) + 2, gr = Math.Max(1, (int)MathF.Ceiling((Bottom - Top) / R)) + 2;
        if (_head.Length != gc * gr) _head = new int[gc * gr];
        Array.Fill(_head, -1);

        for (int i = 0; i < N; i++)
        {
            if (Mode[i] == Gone || T < Rel[i]) continue;
            if (!Launched[i])
            {
                Launched[i] = true; if (InkOnLaunch) Sc[i] = 0;
                float dx = atx[Team[i]] - X[i], dy = aty[Team[i]] - Y[i], dd = MathF.Sqrt(dx * dx + dy * dy); if (dd < 1) dd = 1;
                VX[i] = dx / dd * 140 + (Rand() - 0.5f) * 300; VY[i] = dy / dd * 140 - 60 + (Rand() - 0.5f) * 220; VZ[i] = (Rand() - 0.5f) * 160;
            }
            int cx = (int)((X[i] - Left) / R) + 1, cy = (int)((Y[i] - Top) / R) + 1;
            cx = cx < 0 ? 0 : cx >= gc ? gc - 1 : cx; cy = cy < 0 ? 0 : cy >= gr ? gr - 1 : cy;
            int c = cy * gc + cx; _cell[i] = c; _next[i] = _head[c]; _head[c] = i;
        }

        for (int i = 0; i < N; i++)
        {
            if (Mode[i] == Gone || T < Rel[i]) continue;
            float h = Mode[i] == Assigned ? Clamp01((T - Home[i]) / HRamp) : 0;
            float xi = X[i], yi = Y[i], zi = Z[i], ax = 0, ay = 0, az = 0;
            if (h < 1)
            {
                int n = 0; float mx = 0, my = 0, mz = 0, mvx = 0, mvy = 0, mvz = 0, sx = 0, sy = 0, sz = 0;
                int ci = _cell[i], cxi = ci % gc, cyi = ci / gc; bool full = false;
                for (int o = 0; o < 9 && !full; o++)
                {
                    int xx = cxi + OffX[o], yy = cyi + OffY[o];
                    if (xx < 0 || yy < 0 || xx >= gc || yy >= gr) continue;
                    for (int j = _head[yy * gc + xx]; j >= 0; j = _next[j])
                    {
                        if (j == i || (_split && Team[j] != Team[i])) continue;
                        float dx = X[j] - xi, dy = Y[j] - yi, dz = Z[j] - zi, d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 >= R2) continue;
                        n++; mx += dx; my += dy; mz += dz; mvx += VX[j]; mvy += VY[j]; mvz += VZ[j];
                        if (d2 < S2 && d2 > 0.01f) { float inv = 1 / d2; sx -= dx * inv; sy -= dy * inv; sz -= dz * inv; }
                        if (n >= 7) { full = true; break; }
                    }
                }
                if (n > 0)
                {
                    float inv = 1f / n;
                    ax = (mvx * inv - VX[i]) * AlignK + mx * inv * ck + sx * sk;
                    ay = (mvy * inv - VY[i]) * AlignK + my * inv * ck + sy * sk;
                    az = (mvz * inv - VZ[i]) * AlignK + mz * inv * ck + sz * sk;
                }
                int a = Team[i];
                ax += (atx[a] - xi) * pk + (Rand() - 0.5f) * 220;
                ay += (aty[a] - yi) * pk + (Rand() - 0.5f) * 220;
                az += (atz - zi) * pk;
                if (xi < Left + 6) ax += (Left + 6 - xi) * 40; else if (xi > Right - 6) ax -= (xi - Right + 6) * 40;
                if (yi < Top + 6) ay += (Top + 6 - yi) * 40; else if (yi > Bottom - 6) ay -= (yi - Bottom + 6) * 40;
                float b = 1 - h; ax *= b; ay *= b; az *= b;
            }
            if (h > 0)
            {
                // Arrive: aim at the perch and brake as it nears.
                float wx = (TX[i] - xi) * ArrK, wy = (TY[i] - yi) * ArrK, wz = -zi * ArrK;
                float wm = MathF.Sqrt(wx * wx + wy * wy + wz * wz);
                if (wm > ArrMax) { float k = ArrMax / wm; wx *= k; wy *= k; wz *= k; }
                ax += (wx - VX[i]) * 10 * h; ay += (wy - VY[i]) * 10 * h; az += (wz - VZ[i]) * 10 * h;
            }
            VX[i] += ax * dt; VY[i] += ay * dt; VZ[i] += az * dt;
            float s = MathF.Sqrt(VX[i] * VX[i] + VY[i] * VY[i] + VZ[i] * VZ[i]), lo = VMin * (1 - h), hi = VMax + h * 300;
            if (s > hi) { float k = hi / s; VX[i] *= k; VY[i] *= k; VZ[i] *= k; }
            else if (s < lo && s > 0.001f) { float k = lo / s; VX[i] *= k; VY[i] *= k; VZ[i] *= k; }
            X[i] += VX[i] * dt; Y[i] += VY[i] * dt; Z[i] += VZ[i] * dt;
            if (h > 0)
            {
                float hs = Clamp01((T - Home[i] - HsDelay) / HsDur);
                if (hs > 0)
                {
                    float k = 1 - MathF.Exp(-dt * 28 * hs * hs), ty = Hop != null ? TY[i] - Hop(i, T) : TY[i];
                    X[i] += (TX[i] - X[i]) * k; Y[i] += (ty - Y[i]) * k; Z[i] -= Z[i] * k;
                    VX[i] *= 1 - k; VY[i] *= 1 - k; VZ[i] *= 1 - k;
                }
            }
        }
    }

    /// <summary>Draws the flock. Called on the render thread while the host lock is held.</summary>
    public void Draw(SKCanvas canvas)
    {
        if (Hidden || Palette.Count == 0 || Fade <= 0.02f) return;
        Array.Clear(_used);
        for (int i = 0; i < N; i++)
        {
            if (Mode[i] == Gone) continue;
            bool waiting = T < Rel[i];
            if (waiting && !DrawWaiting) continue;
            float a = Alpha[i] * Fade;
            if (a <= 0.02f) continue;
            int c = Sc[i];
            if (Mode[i] == Assigned && T >= Home[i])
            {
                if (Perch) { float q = (T - Home[i] - HsDelay) / HsDur; c = q > 0.6f ? Tc[i] : q > 0.1f ? MixColor : c; }
                else c = Tc[i];
            }
            if (c >= Palette.Count) c = 0;
            int depth = Z[i] > 24 ? 2 : Z[i] < -24 ? 0 : 1;
            int level = Math.Min(AlphaLevels - 1, (int)MathF.Ceiling(a * AlphaLevels) - 1);
            int key = (c * DepthLevels + depth) * AlphaLevels + level;
            var path = _paths[key] ??= new SKPathBuilder();
            _used[key] = true;
            float x = X[i], y = Y[i], s = waiting ? 0 : MathF.Sqrt(VX[i] * VX[i] + VY[i] * VY[i]);
            if (s < 14) { path.MoveTo(x, y); path.LineTo(x + 0.01f, y); }
            else
            {
                float len = MathF.Min(3.2f, s * 0.0075f) * (0.8f + depth * 0.2f), ux = VX[i] / s * len, uy = VY[i] / s * len;
                path.MoveTo(x - ux, y - uy); path.LineTo(x + ux, y + uy);
            }
        }
        for (int key = 0; key < _paths.Length; key++)
        {
            if (!_used[key]) continue;
            using var path = _paths[key]!.Detach();   // Detach also resets the builder for the next frame
            int level = key % AlphaLevels, cd = key / AlphaLevels, depth = cd % DepthLevels, c = cd / DepthLevels;
            if (c >= Palette.Count) continue;
            _paint.Color = Palette[c].WithAlpha((byte)(255 * (level + 1) / AlphaLevels));
            _paint.StrokeWidth = StrokeByDepth[depth];
            canvas.DrawPath(path, _paint);
        }
    }

    public void Dispose()
    {
        foreach (var p in _paths) p?.Dispose();
        _paint.Dispose();
    }
}
