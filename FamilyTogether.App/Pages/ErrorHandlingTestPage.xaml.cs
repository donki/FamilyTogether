using FamilyTogether.App.Services;

namespace FamilyTogether.App.Pages;

public partial class ErrorHandlingTestPage : ContentPage
{
    private readonly ErrorHandlingTestService _testService;
    
    public ErrorHandlingTestPage(ErrorHandlingTestService testService)
    {
        InitializeComponent();
        _testService = testService;
    }
    
    private async void OnTestConnectivityClicked(object sender, EventArgs e)
    {
        try
        {
            ResultsLabel.Text = "Testing connectivity...";
            var result = await _testService.TestNetworkConnectivityAsync();
            ResultsLabel.Text = result;
        }
        catch (Exception ex)
        {
            ResultsLabel.Text = $"Error: {ex.Message}";
        }
    }
    
    private void OnTestOfflineStorageClicked(object sender, EventArgs e)
    {
        try
        {
            ResultsLabel.Text = "Testing offline storage...";
            var result = _testService.TestOfflineStorage();
            ResultsLabel.Text = result;
        }
        catch (Exception ex)
        {
            ResultsLabel.Text = $"Error: {ex.Message}";
        }
    }
    
    private void OnTestRetryPolicyClicked(object sender, EventArgs e)
    {
        try
        {
            ResultsLabel.Text = "Testing retry policy...";
            var result = _testService.TestRetryPolicy();
            ResultsLabel.Text = result;
        }
        catch (Exception ex)
        {
            ResultsLabel.Text = $"Error: {ex.Message}";
        }
    }
    
    private void OnTestErrorClassificationClicked(object sender, EventArgs e)
    {
        try
        {
            ResultsLabel.Text = "Testing error classification...";
            var result = _testService.TestErrorClassification();
            ResultsLabel.Text = result;
        }
        catch (Exception ex)
        {
            ResultsLabel.Text = $"Error: {ex.Message}";
        }
    }
    
    private async void OnSimulateFailureClicked(object sender, EventArgs e)
    {
        try
        {
            ResultsLabel.Text = "Simulating network failure...";
            var result = await _testService.SimulateNetworkFailureAsync();
            ResultsLabel.Text = result;
        }
        catch (Exception ex)
        {
            ResultsLabel.Text = $"Error: {ex.Message}";
        }
    }
    
    private void OnGetStatusReportClicked(object sender, EventArgs e)
    {
        try
        {
            ResultsLabel.Text = "Generating status report...";
            var result = _testService.GetStatusReport();
            ResultsLabel.Text = result;
        }
        catch (Exception ex)
        {
            ResultsLabel.Text = $"Error: {ex.Message}";
        }
    }
    
    private async void OnClearTestDataClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await DisplayAlert("Clear Test Data", 
                "This will clear all test data and pending offline data. Continue?", 
                "Yes", "No");
                
            if (result)
            {
                _testService.CleanupTestData();
                ResultsLabel.Text = "Test data cleared successfully.";
            }
        }
        catch (Exception ex)
        {
            ResultsLabel.Text = $"Error: {ex.Message}";
        }
    }
}