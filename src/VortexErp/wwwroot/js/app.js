// Ayudas mínimas para la UI de Vortex ERP (posicionar menús flotantes y enfocar campos).
window.vortex = {
    rect: (el) => {
        if (!el) return [0, 0, 0, 0, window.innerWidth, window.innerHeight];
        const r = el.getBoundingClientRect();
        return [r.top, r.left, r.bottom, r.right, window.innerWidth, window.innerHeight];
    },
    focus: (el) => { if (el) el.focus(); },
};
