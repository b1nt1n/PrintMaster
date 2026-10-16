using PrintMaster.Services;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Controls;

// The inner TextBox is a draft. Only a successful commit writes to the bound source.
public sealed class NumericField : UserControl
{
    private readonly TextBlock _message=new() { TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Firebrick,FontSize=11,Margin=new(0,4,0,0),Visibility=Visibility.Collapsed };
    private readonly TextBlock _unit=new() { Foreground=Brushes.DimGray,Margin=new(5,0,0,0),VerticalAlignment=VerticalAlignment.Center };
    private string _committed="";
    private bool _updating,_committing;
    public TextBox Input { get; }=new() { MinHeight=32,Padding=new(3,6,3,6),VerticalContentAlignment=VerticalAlignment.Center };
    private NumericInputRules Rules=>new(Minimum,Maximum,Unit,IsInteger,MinimumExclusive,MaximumExclusive,AllowEmpty,AllowPercent);
    public bool HasError=>Error.Length!=0;
    public bool HasDraft=>Input.Text!=_committed;

    public NumericField()
    {
        Focusable=false; var root=new StackPanel(); var row=new Grid();
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
        Grid.SetColumn(_unit,1); row.Children.Add(Input); row.Children.Add(_unit); root.Children.Add(row); root.Children.Add(_message); Content=root;
        Input.TextChanged+=(_,_)=>{ if(!_updating && !_committing) SetError(""); };
        Input.PreviewKeyDown+=(_,e)=>
        { if(e.Key==Key.Enter && Keyboard.Modifiers==ModifierKeys.None) { Commit(); e.Handled=true; }
          else if(e.Key==Key.Escape && Keyboard.Modifiers==ModifierKeys.None) { CancelDraft(); e.Handled=true; } };
        Input.LostKeyboardFocus+=(_,_)=>{ if(IsEnabled && Commit()) ResetDraft(Text); };
        Input.GotKeyboardFocus+=(_,_)=>
        { if(!HasDraft) { _committed=Text; SetDraft(Text); } SyncAppearance(); };
        Loaded+=(_,_)=>{ SyncAppearance(); if(!HasDraft) ResetFromSource(); };
        // Reset at the context boundary. A deferred reset can erase typing after an Expander opens.
        DataContextChanged+=(_,_)=>ResetFromSource();
        IsVisibleChanged+=(_,e)=>{ if(e.NewValue is false) CancelDraft(); };
    }

    public static readonly DependencyProperty TextProperty=DependencyProperty.Register(nameof(Text),typeof(string),typeof(NumericField),
        new FrameworkPropertyMetadata("",FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,TextChanged,null,false,UpdateSourceTrigger.Explicit));
    public string Text { get=>(string)GetValue(TextProperty); set=>SetValue(TextProperty,value); }
    private static void TextChanged(DependencyObject sender,DependencyPropertyChangedEventArgs e)
    { var field=(NumericField)sender; if(!field._committing) field.ResetDraft((string?)e.NewValue ?? ""); }

    private static DependencyProperty Option(string name,Type type,object value)=>DependencyProperty.Register(name,type,typeof(NumericField),new PropertyMetadata(value,AppearanceChanged));
    public static readonly DependencyProperty MinimumProperty=Option(nameof(Minimum),typeof(double),0d);
    public static readonly DependencyProperty MaximumProperty=Option(nameof(Maximum),typeof(double),10000d);
    public static readonly DependencyProperty MinimumExclusiveProperty=Option(nameof(MinimumExclusive),typeof(bool),false);
    public static readonly DependencyProperty MaximumExclusiveProperty=Option(nameof(MaximumExclusive),typeof(bool),false);
    public static readonly DependencyProperty IsIntegerProperty=Option(nameof(IsInteger),typeof(bool),false);
    public static readonly DependencyProperty AllowEmptyProperty=Option(nameof(AllowEmpty),typeof(bool),false);
    public static readonly DependencyProperty AllowPercentProperty=Option(nameof(AllowPercent),typeof(bool),false);
    public static readonly DependencyProperty UnitProperty=Option(nameof(Unit),typeof(string),"");
    public static readonly DependencyProperty ShowInlineErrorProperty=Option(nameof(ShowInlineError),typeof(bool),true);
    public static readonly DependencyProperty EquivalentAngleProperty=Option(nameof(EquivalentAngle),typeof(bool),false);
    public static readonly DependencyProperty RejectionMessageProperty=Option(nameof(RejectionMessage),typeof(string),"");
    public static readonly DependencyProperty TextAlignmentProperty=Option(nameof(TextAlignment),typeof(System.Windows.TextAlignment),System.Windows.TextAlignment.Left);
    public static readonly DependencyProperty ContextKeyProperty=DependencyProperty.Register(nameof(ContextKey),typeof(object),typeof(NumericField),new PropertyMetadata(null,(d,_)=>((NumericField)d).ResetFromSource()));
    private static readonly DependencyPropertyKey ErrorKey=DependencyProperty.RegisterReadOnly(nameof(Error),typeof(string),typeof(NumericField),new PropertyMetadata(""));
    public static readonly DependencyProperty ErrorProperty=ErrorKey.DependencyProperty;
    public string Error=>(string)GetValue(ErrorProperty);
    public double Minimum { get=>(double)GetValue(MinimumProperty); set=>SetValue(MinimumProperty,value); }
    public double Maximum { get=>(double)GetValue(MaximumProperty); set=>SetValue(MaximumProperty,value); }
    public bool MinimumExclusive { get=>(bool)GetValue(MinimumExclusiveProperty); set=>SetValue(MinimumExclusiveProperty,value); }
    public bool MaximumExclusive { get=>(bool)GetValue(MaximumExclusiveProperty); set=>SetValue(MaximumExclusiveProperty,value); }
    public bool IsInteger { get=>(bool)GetValue(IsIntegerProperty); set=>SetValue(IsIntegerProperty,value); }
    public bool AllowEmpty { get=>(bool)GetValue(AllowEmptyProperty); set=>SetValue(AllowEmptyProperty,value); }
    public bool AllowPercent { get=>(bool)GetValue(AllowPercentProperty); set=>SetValue(AllowPercentProperty,value); }
    public string Unit { get=>(string)GetValue(UnitProperty); set=>SetValue(UnitProperty,value); }
    public bool ShowInlineError { get=>(bool)GetValue(ShowInlineErrorProperty); set=>SetValue(ShowInlineErrorProperty,value); }
    public bool EquivalentAngle { get=>(bool)GetValue(EquivalentAngleProperty); set=>SetValue(EquivalentAngleProperty,value); }
    public string RejectionMessage { get=>(string)GetValue(RejectionMessageProperty); set=>SetValue(RejectionMessageProperty,value); }
    public System.Windows.TextAlignment TextAlignment { get=>(System.Windows.TextAlignment)GetValue(TextAlignmentProperty); set=>SetValue(TextAlignmentProperty,value); }
    public object? ContextKey { get=>GetValue(ContextKeyProperty); set=>SetValue(ContextKeyProperty,value); }
    private static void AppearanceChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)=>((NumericField)d).SyncAppearance();

    public bool Commit()
    {
        if(!IsEnabled) return true;
        var draft=Input.Text;
        if(!Rules.TryValidate(HasDraft ? draft : Text,out var value,out var error)) { SetError(error); return false; }
        if(!HasDraft) { SetError(""); return true; }
        _committing=true;
        try
        {
            var normalized=AllowEmpty && string.IsNullOrWhiteSpace(draft) ? "" : value.ToString("G",CultureInfo.InvariantCulture);
            SetCurrentValue(TextProperty,normalized);
            var binding=GetBindingExpression(TextProperty); binding?.UpdateSource(); binding?.UpdateTarget();
            var accepted=AllowEmpty && Text.Length==0 || NumericInputRules.TryParse(Text,out var actual,AllowPercent)
                && (Math.Abs(actual-value)<=Math.Max(1,Math.Abs(value))*1e-12 || EquivalentAngle && Math.Abs(ObjectTransform.NormalizeAngle(actual-value))<1e-9);
            if(Validation.GetHasError(this) || !accepted)
            {
                SetError(string.IsNullOrWhiteSpace(RejectionMessage) ? "Значение не применено. "+Rules.Range : RejectionMessage);
                SetDraft(draft); return false;
            }
            ResetDraft(Text); return true;
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
        { SetError(ex.Message); SetDraft(draft); return false; }
        finally { _committing=false; }
    }

    public void CancelDraft()=>ResetFromSource();
    public void FocusInput() { Input.Focus(); UpdateLayout(); BringIntoView(); }
    private void ResetFromSource()
    {
        if(_committing) return;
        var binding=GetBindingExpression(TextProperty);
        if(binding!=null) { Validation.ClearInvalid(binding); binding.UpdateTarget(); }
        ResetDraft(Text ?? "");
    }
    private void ResetDraft(string text)
    { _committed=Input.IsKeyboardFocusWithin ? text : Brief(text); SetDraft(_committed); SetError(""); }
    private static string Brief(string text)
    {
        if(!NumericInputRules.TryParse(text,out var value,true)) return text;
        // Small non-zero values must never appear as zero. Formatting never writes to the model.
        return value!=0 && Math.Abs(value)<.0005 ? value.ToString("G",CultureInfo.CurrentCulture) : value.ToString("0.###",CultureInfo.CurrentCulture);
    }
    private void SetDraft(string text)
    { _updating=true; try { Input.Text=text; } finally { _updating=false; } }
    private void SetError(string text) { SetValue(ErrorKey,text); SyncAppearance(); }
    private void SyncAppearance()
    {
        if(Input==null) return;
        _unit.Text=Unit; _unit.Visibility=Unit.Length==0 ? Visibility.Collapsed : Visibility.Visible;
        _message.Text=HasError ? Error : Rules.Range; _message.Foreground=HasError ? Brushes.Firebrick : Brushes.DimGray;
        _message.Visibility=ShowInlineError && (HasError || Input.IsKeyboardFocusWithin) ? Visibility.Visible : Visibility.Collapsed;
        Input.TextAlignment=TextAlignment; Input.BorderBrush=HasError ? Brushes.Firebrick : TryFindResource("Line") as Brush ?? SystemColors.ControlDarkBrush;
        var help=Rules.Range+" Enter — применить; Esc — отменить ввод. Точка и запятая допустимы.";
        Input.ToolTip=HasError ? Error : help;
        AutomationProperties.SetName(Input,AutomationProperties.GetName(this));
        AutomationProperties.SetHelpText(Input,HasError ? Error : help);
    }

    public static bool CommitVisible(DependencyObject root)
    {
        NumericField? first=null;
        foreach(var field in Fields(root).Where(f=>f.IsVisible && f.IsEnabled)) if(!field.Commit()) first ??=field;
        if(first==null) return true; first.FocusInput(); return false;
    }
    private static IEnumerable<NumericField> Fields(DependencyObject root)
    {
        if(root is NumericField field) { yield return field; yield break; }
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in Fields(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
}
