using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Controls;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Data;
using Xunit;

namespace PrintMaster.Tests;

public sealed class NumericFieldTests
{
    public sealed class NumberModel : ObservableObject
    {
        private double _value=4;
        public double Limit { get; set; }=1000;
        public int Writes { get; private set; }
        public double Value { get=>_value; set { Writes++; if(value<=Limit) SetProperty(ref _value,value); } }
    }
    private static NumericField Field(NumberModel model)=>Create(model);
    private static NumericField Create(NumberModel model)
    {
        var field=new NumericField { Minimum=0,MinimumExclusive=true,Maximum=1000,Unit="мм" };
        field.SetBinding(NumericField.TextProperty,new Binding(nameof(NumberModel.Value)) { Source=model,Mode=BindingMode.TwoWay,Converter=new NumericValueConverter(),UpdateSourceTrigger=UpdateSourceTrigger.Explicit,ValidatesOnExceptions=true });
        field.GetBindingExpression(NumericField.TextProperty)!.UpdateTarget(); return field;
    }
    [Fact]
    public Task IntermediateInputIsKeptUntilCommitAndEscapeRestoresTheSource()=>OnSta(()=>
    {
        var model=new NumberModel(); var field=Field(model);
        field.Input.Text="-"; Assert.Equal(4,model.Value); Assert.Equal(0,model.Writes); Assert.True(field.HasDraft);
        Assert.False(field.Commit()); Assert.Equal("-",field.Input.Text); Assert.Contains("Введите",field.Error); Assert.Equal(4,model.Value);
        field.CancelDraft(); Assert.Equal("4",field.Input.Text); Assert.False(field.HasError); Assert.Equal(0,model.Writes);
        field.Input.Text="12,"; Assert.Equal(4,model.Value); Assert.True(field.Commit()); Assert.Equal(12,model.Value); Assert.Equal(1,model.Writes);
    });
    [Fact]
    public Task AcceptedDecimalUpdatesOnceAndUntouchedPrecisionIsNotRounded()=>OnSta(()=>
    {
        var model=new NumberModel(); var field=Field(model);
        field.Input.Text="12,5"; Assert.True(field.Commit()); Assert.Equal(12.5,model.Value); Assert.Equal(1,model.Writes);
        Assert.True(field.Commit()); Assert.Equal(1,model.Writes);
        model.Value=1.234567891234; var before=model.Writes; field.GetBindingExpression(NumericField.TextProperty)!.UpdateTarget();
        Assert.True(field.Commit()); Assert.Equal(before,model.Writes); Assert.Equal(1.234567891234,model.Value);
    });
    [Fact]
    public Task RejectedViewModelValuePreservesDraftAndReportsItsReason()=>OnSta(()=>
    {
        var model=new NumberModel { Limit=5 }; var field=Field(model); field.RejectionMessage="Высоты не хватает для всего текста.";
        field.Input.Text="8,5"; Assert.False(field.Commit()); Assert.Equal(4,model.Value); Assert.Equal("8,5",field.Input.Text); Assert.Equal(field.RejectionMessage,field.Error);
        field.Input.Text="4,5"; Assert.True(field.Commit()); Assert.Equal(4.5,model.Value); Assert.False(field.HasError);
    });
    [Fact]
    public Task FractionalCopiesDoNotReachIntegerConversion()=>OnSta(()=>
    {
        var model=new NumberModel(); var field=Field(model); field.Minimum=1; field.Maximum=999; field.IsInteger=true;
        field.Input.Text="2,5"; Assert.False(field.Commit()); Assert.Equal(4,model.Value); Assert.Equal(0,model.Writes); Assert.Equal("2,5",field.Input.Text);
        field.Input.Text="2.0"; Assert.True(field.Commit()); Assert.Equal(2,model.Value);
    });
    [Fact]
    public Task ContextChangeNeverAppliesOldDraftToAnotherObject()=>OnSta(()=>
    {
        var a=new NumberModel(); var field=Field(a); field.Input.Text="0"; Assert.False(field.Commit());
        var b=new NumberModel(); field.SetBinding(NumericField.TextProperty,new Binding(nameof(NumberModel.Value)) { Source=b,Mode=BindingMode.TwoWay,Converter=new NumericValueConverter(),UpdateSourceTrigger=UpdateSourceTrigger.Explicit });
        field.ContextKey=Guid.NewGuid(); Assert.Equal("4",field.Input.Text); Assert.False(field.HasError); Assert.True(field.Commit()); Assert.Equal(0,b.Writes); Assert.Equal(0,a.Writes);
    });
    [Fact]
    public Task InactiveDisplayIsBriefWithoutRoundingTheStoredValue()=>OnSta(()=>
    {
        var model=new NumberModel { Value=1.234567891234 }; var writes=model.Writes; var field=Field(model);
        Assert.Equal("1.235",field.Input.Text.Replace(',','.')); Assert.True(field.Commit()); Assert.Equal(writes,model.Writes); Assert.Equal(1.234567891234,model.Value);
        model.Value=.00000000123; field.GetBindingExpression(NumericField.TextProperty)!.UpdateTarget();
        Assert.True(PrintMaster.Services.NumericInputRules.TryParse(field.Input.Text,out var tiny)); Assert.Equal(model.Value,tiny); Assert.True(field.Commit());
    });
    private static Task OnSta(Action action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{ try { action(); completion.SetResult(); } catch(Exception ex) { completion.SetException(ex); } }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
