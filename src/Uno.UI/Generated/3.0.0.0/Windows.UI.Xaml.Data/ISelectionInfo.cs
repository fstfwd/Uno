#pragma warning disable 108 // new keyword hiding
#pragma warning disable 114 // new keyword hiding
namespace Windows.UI.Xaml.Data
{
	#if __ANDROID__ || __IOS__ || NET46 || __WASM__
	[global::Uno.NotImplemented]
	#endif
	public  partial interface ISelectionInfo 
	{
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		void SelectRange( global::Windows.UI.Xaml.Data.ItemIndexRange itemIndexRange);
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		void DeselectRange( global::Windows.UI.Xaml.Data.ItemIndexRange itemIndexRange);
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		bool IsSelected( int index);
		#endif
		#if __ANDROID__ || __IOS__ || NET46 || __WASM__
		global::System.Collections.Generic.IReadOnlyList<global::Windows.UI.Xaml.Data.ItemIndexRange> GetSelectedRanges();
		#endif
	}
}
