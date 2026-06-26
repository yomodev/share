namespace NxtUI.Models;

public enum WsDir { H, V }  // H = left/right, V = top/bottom

public abstract class WsNode
{
    public abstract WsLeaf? FindLeaf(Func<WsLeaf, bool> predicate);
    public abstract WsNode  Replace(WsLeaf target, WsNode replacement);
    public abstract WsNode? Remove(WsLeaf target);
}

public sealed class WsLeaf(string? path, string env = "", string? fileName = null) : WsNode
{
    public readonly Guid   Id       = Guid.NewGuid();
    public string?         Path     = path;
    public string          Env      = env;
    public string?         FileName = fileName;

    public override WsLeaf? FindLeaf(Func<WsLeaf, bool> p) => p(this) ? this : null;
    public override WsNode  Replace(WsLeaf t, WsNode r)     => ReferenceEquals(this, t) ? r : this;
    public override WsNode? Remove(WsLeaf t)                => ReferenceEquals(this, t) ? null : this;
}

public sealed class WsSplit(WsDir dir, double ratio, WsNode a, WsNode b) : WsNode
{
    public WsDir   Dir   = dir;
    public double  Ratio = ratio;   // fraction of space given to A (0..1)
    public WsNode  A     = a;
    public WsNode  B     = b;

    public override WsLeaf? FindLeaf(Func<WsLeaf, bool> p) =>
        A.FindLeaf(p) ?? B.FindLeaf(p);

    public override WsNode Replace(WsLeaf t, WsNode r) =>
        new WsSplit(Dir, Ratio, A.Replace(t, r), B.Replace(t, r));

    public override WsNode? Remove(WsLeaf t)
    {
        var na = A.Remove(t);
        var nb = B.Remove(t);
        if (na is null) return nb;
        if (nb is null) return na;
        return new WsSplit(Dir, Ratio, na, nb);
    }
}
