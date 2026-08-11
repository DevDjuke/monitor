(() => {
    'use strict';

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
})();
