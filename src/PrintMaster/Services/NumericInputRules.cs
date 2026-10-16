using System.Globalization;

namespace PrintMaster.Services;

public sealed record NumericInputRules(double Minimum,double Maximum,string Unit="",bool Integer=false,
    bool MinimumExclusive=false,bool MaximumExclusive=false,bool AllowEmpty=false,bool AllowPercent=false)
{
    public string Range
    {
        get
        {
            var min=Minimum.ToString("G",CultureInfo.CurrentCulture); var max=Maximum.ToString("G",CultureInfo.CurrentCulture);
            var bounds=MinimumExclusive ? $"больше {min} и {(MaximumExclusive ? "меньше" : "не больше")} {max}"
                : MaximumExclusive ? $"от {min} до {max}, не включая {max}" : $"от {min} до {max}";
            return $"{(Integer ? "Целое число" : "Число")} {bounds}{(Unit.Length==0 ? "" : " "+Unit)}.";
        }
    }

    public bool TryValidate(string? text,out double value,out string error)
    {
        value=0; error="";
        if(AllowEmpty && string.IsNullOrWhiteSpace(text)) return true;
        if(!TryParse(text,out value,AllowPercent)) { error="Введите число. "+Range; return false; }
        if(Integer && value!=Math.Truncate(value)) { error="Дробное значение здесь недопустимо. "+Range; return false; }
        if(value<Minimum || value>Maximum || MinimumExclusive && value==Minimum || MaximumExclusive && value==Maximum)
        { error=Range; return false; }
        return true;
    }

    // Do not guess thousands separators: 1,234.5 or 1 234 are ambiguous here.
    public static bool TryParse(string? text,out double value,bool allowPercent=false)
    {
        value=0; if(text==null) return false;
        var normalized=text.Trim();
        if(allowPercent && normalized.EndsWith('%')) normalized=normalized[..^1].TrimEnd();
        return double.TryParse(normalized.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out value) && double.IsFinite(value);
    }
}
