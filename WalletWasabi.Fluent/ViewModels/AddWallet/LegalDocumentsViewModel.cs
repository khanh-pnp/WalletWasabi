using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.AddWallet
{
	public class LegalDocumentsViewModel : RoutableViewModel
	{
		public LegalDocumentsViewModel(string content, bool backOnNext)
		{
			Content = content;

			NextCommand = backOnNext ? BackCommand : CancelCommand;
		}

		public override NavigationTarget DefaultTarget => NavigationTarget.DialogScreen;

		public string Content { get; }
	}
}