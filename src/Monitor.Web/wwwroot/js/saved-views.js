(() => {
    'use strict';

    function syncSavedViewControls() {
        const currentUrl = `${window.location.pathname}${window.location.search}`;

        for (const picker of document.querySelectorAll('[data-saved-view-picker]')) {
            const matching = [...picker.options].find(option => option.value === currentUrl);
            picker.value = matching?.value || '';
        }

        for (const input of document.querySelectorAll('[data-saved-view-current-query]')) {
            input.value = window.location.search;
        }

        for (const input of document.querySelectorAll('[data-saved-view-return-url]')) {
            input.value = currentUrl;
        }
    }

    document.addEventListener('monitor:saved-view-url-changed', syncSavedViewControls);
    window.addEventListener('popstate', syncSavedViewControls);

    document.addEventListener('change', event => {
        const picker = event.target.closest?.('[data-saved-view-picker]');
        if (!picker || !picker.value) return;
        window.location.assign(picker.value);
    });

    document.addEventListener('submit', event => {
        const form = event.target.closest?.('form[data-confirm]');
        if (!form) return;

        const message = form.dataset.confirm;
        if (message && !window.confirm(message)) {
            event.preventDefault();
        }
    });

    syncSavedViewControls();
})();
