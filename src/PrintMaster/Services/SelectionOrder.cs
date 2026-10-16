using System.Collections.Immutable;

namespace PrintMaster.Services;

// -1/+1 move one layer; -2/+2 move to the bottom/top. Relative order is stable.
public static class SelectionOrder
{
    public static ImmutableArray<T> Move<T>(IReadOnlyList<T> items,Func<T,bool> selected,int direction)
    {
        if(direction is not (-2 or -1 or 1 or 2)) throw new ArgumentOutOfRangeException(nameof(direction));
        if(Math.Abs(direction)==2)
            return (direction>0 ? items.Where(x=>!selected(x)).Concat(items.Where(selected))
                : items.Where(selected).Concat(items.Where(x=>!selected(x)))).ToImmutableArray();
        var result=items.ToArray();
        if(direction>0)
        {
            for(var i=result.Length-2;i>=0;i--)
                if(selected(result[i]) && !selected(result[i+1])) (result[i],result[i+1])=(result[i+1],result[i]);
        }
        else
        {
            for(var i=1;i<result.Length;i++)
                if(selected(result[i]) && !selected(result[i-1])) (result[i],result[i-1])=(result[i-1],result[i]);
        }
        return result.ToImmutableArray();
    }
}
