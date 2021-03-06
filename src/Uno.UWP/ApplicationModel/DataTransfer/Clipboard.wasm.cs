﻿using Uno.Foundation;

namespace Windows.ApplicationModel.DataTransfer
{
	public partial class Clipboard
	{
		public static void SetContent(DataPackage content)
		{
			var text = WebAssemblyRuntime.EscapeJs(content.Text);
			var command = $"Uno.Utils.Clipboard.setText(\"{text}\");";
			WebAssemblyRuntime.InvokeJS(command);
		}
	}
}