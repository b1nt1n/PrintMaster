namespace PrintMaster.Services;

// Russian cardinal quantities: 1 page, 2–4 pages, 5–20 pages; 11–14
// retain the many form even when the last digit is 1–4.
public static class RussianQuantity
{
    public static string Format(int count,string one,string few,string many)
    {
        var lastTwo=Math.Abs(count%100);var last=Math.Abs(count%10);
        var word=lastTwo is >=11 and <=14 ? many : last==1 ? one : last is >=2 and <=4 ? few : many;
        return $"{count} {word}";
    }
}
