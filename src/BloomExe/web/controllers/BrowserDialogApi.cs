﻿using Bloom.Api;
using Bloom.MiscUI;

namespace Bloom.web.controllers
{
	class BrowserDialogApi
	{
		public void RegisterWithApiHandler(BloomApiHandler apiHandler)
		{
			apiHandler.RegisterEndpointHandler("dialog/close",
				(ApiRequest request) =>
				{
					BrowserDialog.CloseDialog();
					request.PostSucceeded();
				}, true);
		}
	}
}
