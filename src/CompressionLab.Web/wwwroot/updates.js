(() => {
    const element = id => document.getElementById(id);
    let latestStatus;
    let restarting = false;

    function show(status) {
        latestStatus = status;
        element('installed-version').textContent = status.currentVersion;
        element('update-message').textContent = status.message;
        element('update-notes').textContent = status.notes || '';
        element('check-updates').disabled = ['checking', 'downloading', 'installing', 'unconfigured'].includes(status.state);
        element('install-update').hidden = status.state !== 'available';
        element('update-progress').hidden = status.state !== 'downloading';
        element('update-progress').max = status.totalBytes || 1;
        element('update-progress').value = status.downloadedBytes;
        element('update-banner').hidden = !['available', 'downloading', 'installing'].includes(status.state);
        element('update-banner').textContent = status.state === 'available'
            ? `Доступно обновление ${status.latestVersion}`
            : status.message;
    }

    async function readStatus() {
        if (restarting) return;
        try {
            const response = await fetch('api/updates');
            if (response.ok) show(await response.json());
        } catch { /* The main app handles connection failures outside a restart. */ }
    }

    element('update-banner').onclick = () => document.querySelector('[data-view="updates"]').click();
    element('check-updates').onclick = async () => {
        element('check-updates').disabled = true;
        element('update-message').textContent = 'Проверяем последнюю версию…';
        try {
            const response = await fetch('api/updates/check', { method: 'POST' });
            if (!response.ok) throw Error('Не удалось проверить обновления');
            show(await response.json());
        } catch (error) {
            element('update-message').textContent = error.message;
            element('check-updates').disabled = false;
        }
    };

    element('install-update').onclick = async () => {
        const expectedVersion = latestStatus.latestVersion;
        element('install-update').disabled = true;
        try {
            const response = await fetch('api/updates/install', { method: 'POST' });
            if (!response.ok) {
                const error = await response.json();
                throw Error(error.error || 'Установка не началась');
            }

            restarting = true;
            window.labRestarting = true;
            element('install-update').hidden = true;
            const deadline = Date.now() + 12 * 60 * 1000;
            while (Date.now() < deadline) {
                await new Promise(resolve => setTimeout(resolve, 1000));
                try {
                    const statusResponse = await fetch('api/updates');
                    if (!statusResponse.ok) continue;
                    const status = await statusResponse.json();
                    show(status);
                    if (status.currentVersion === expectedVersion) {
                        location.reload();
                        return;
                    }
                    if (status.state === 'error') throw Error(status.message);
                } catch (error) {
                    if (error instanceof TypeError) {
                        element('update-message').textContent = 'Перезапускаем приложение…';
                        continue;
                    }
                    throw error;
                }
            }
            throw Error('Приложение не ответило после обновления. Запустите EXE вручную.');
        } catch (error) {
            element('update-message').textContent = error.message;
            element('install-update').disabled = false;
            restarting = false;
            window.labRestarting = false;
        }
    };

    readStatus();
    setInterval(readStatus, 2500);
})();
