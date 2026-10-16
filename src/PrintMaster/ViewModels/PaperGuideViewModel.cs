using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Globalization;

namespace PrintMaster.ViewModels;

public enum PaperGuideStep { Load,Reload,ChangeStock,Preview }
public partial class PaperGuideViewModel : ObservableObject
{
    public PaperGuideViewModel(PrintSettingsSnapshot settings,int feeds,PaperGuideStep step,PrinterPaperCalibration? calibration=null,bool allowCalibration=true)
    {
        Settings=settings; Feeds=feeds; Step=step; CanCalibrate=allowCalibration;
        offsetXText=(calibration?.OffsetXmm ?? settings.PrintOffsetXmm).ToString("0.###",CultureInfo.CurrentCulture);
        offsetYText=(calibration?.OffsetYmm ?? settings.PrintOffsetYmm).ToString("0.###",CultureInfo.CurrentCulture);
        outputFaceDown=calibration?.OutputFaceDown ?? true; backRotationDegrees=calibration?.BackRotationDegrees ?? 0;
        Validate();
    }
    public PrintSettingsSnapshot Settings { get; }
    public PaperGuideStep Step { get; }
    public int Feeds { get; }
    public bool CanCalibrate { get; }
    public bool IsKnownP1102=>Settings.PrinterName.Contains("P1102",StringComparison.OrdinalIgnoreCase);
    public bool UseShortMediaExtender=>IsKnownP1102 && Settings.PaperSizeMm.Height<185;
    public string DiagramTitle=>IsKnownP1102 ? "Схема загрузки бумаги в лоток P1102" : "Условная схема бумаги: способ загрузки проверьте по руководству принтера";
    public bool IsReload=>Step==PaperGuideStep.Reload;
    public bool CanHide=>Step==PaperGuideStep.Load;
    public string Title=>IsReload ? "Переверните пачку для второго прохода" : Step==PaperGuideStep.ChangeStock ? "Замените бумагу для следующей части" : Settings.Paper==PaperPreset.Notebook ? "Как загрузить тетрадный лист" : "Как загрузить бумагу";
    private string PaperName=>Settings.Notebook?.Name ?? (Settings.Paper switch { PaperPreset.A4=>"A4",PaperPreset.A5=>"A5",PaperPreset.Notebook=>"Тетрадный лист",_=>"Пользовательский формат" });
    public string Summary=>$"{Settings.PrinterName}\n{PaperName} · подача {Settings.PaperSizeMm.Width:g} × {Settings.PaperSizeMm.Height:g} мм · {RussianQuantity.Format(Feeds,"лист","листа","листов")}";
    public string Instruction
    {
        get
        {
            if(!IsKnownP1102) return IsReload
                ? "Дождитесь выхода всех листов первого прохода и сохраните порядок пачки. Сторону и край для повторной загрузки проверьте в руководстве выбранного принтера. Эта условная схема не определяет способ подачи другой модели. После загрузки нажмите «Печатать оборот»."
                : "Сторону листа, входящий край и положение направляющих проверьте в руководстве выбранного принтера. Эта условная схема показывает выбранный размер бумаги; способ подачи P1102 к другой модели не применяется.";
            var instruction=IsReload
                ? "Дождитесь, пока все листы первого прохода выйдут из принтера. Сохраните порядок пачки и загрузите ее напечатанной стороной вниз. Снова подайте тем же краем, который входил первым. После загрузки нажмите «Печатать оборот»."
                : $"Загрузите бумагу по центру лотка краем шириной {Settings.PaperSizeMm.Width:g} мм вперед. Сторона, на которой нужен новый оттиск, должна смотреть вверх. Подведите боковые и переднюю направляющие к краям листа без изгиба бумаги.";
            instruction+=" Альбомная ориентация поворачивает содержимое; бумага в лотке остается в показанном положении.";
            if(UseShortMediaExtender) instruction+=" Для длины подачи меньше 185 мм установите насадку для короткой бумаги на переднюю направляющую. После печати верните насадку в ее место хранения слева.";
            return instruction;
        }
    }
    public string ActionLabel=>Step==PaperGuideStep.Preview ? "Готово" : IsReload ? "Печатать оборот" : "Печать";
    public string ActionIcon=>Step==PaperGuideStep.Preview ? "check" : "printer";
    public IReadOnlyList<Choice<bool>> StackChoices { get; }=[new(true,"Напечатанной стороной вниз"),new(false,"Напечатанной стороной вверх")];
    public IReadOnlyList<Choice<int>> RotationChoices { get; }=[new(0,"Верх оборота совпадает"),new(180,"Повернуть оборот на 180°")];
    [ObservableProperty] private bool hideAgain;
    [ObservableProperty] private string offsetXText="0";
    [ObservableProperty] private string offsetYText="0";
    [ObservableProperty] private bool outputFaceDown=true;
    [ObservableProperty] private int backRotationDegrees;
    public string Error { get; private set; }="";
    public bool CanContinue=>Error.Length==0;
    public PrinterPaperCalibration? Calibration { get; private set; }
    partial void OnOffsetXTextChanged(string value)=>Validate();
    partial void OnOffsetYTextChanged(string value)=>Validate();
    partial void OnOutputFaceDownChanged(bool value)=>Validate();
    partial void OnBackRotationDegreesChanged(int value)=>Validate();
    private static double Number(string text)=>double.TryParse(text.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var v) ? v : double.NaN;
    private void Validate()
    {
        Error=""; Calibration=null;
        try
        {
            var value=new PrinterPaperCalibration(Settings.PrinterName,Settings.PaperSizeMm,Settings.Landscape,
                Number(OffsetXText),Number(OffsetYText),OutputFaceDown,BackRotationDegrees);
            value.Validate(); Calibration=value;
        }
        catch(ArgumentException ex) { Error=ex.Message; }
        OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(CanContinue));
    }
}
