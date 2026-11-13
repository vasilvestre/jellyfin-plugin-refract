const pluginUniqueId = 'c9f4a1d2-7b3e-4f6a-9d21-3e5c8b0a7f14';

export default function (view) {
    view.addEventListener('viewshow', function () {
        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
            view.querySelector('#apiKey').value = config.ApiKey || '';
        });
    });

    view.querySelector('#saveKey').addEventListener('click', function () {
        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
            config.ApiKey = view.querySelector('#apiKey').value.trim();
            ApiClient.updatePluginConfiguration(pluginUniqueId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    });
}
