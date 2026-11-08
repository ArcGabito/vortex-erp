// Ayudas mínimas para la UI de Vortex ERP (posicionar menús flotantes y enfocar campos).
window.vortex = {
    rect: (el) => {
        if (!el) return [0, 0, 0, 0, window.innerWidth, window.innerHeight];
        const r = el.getBoundingClientRect();
        return [r.top, r.left, r.bottom, r.right, window.innerWidth, window.innerHeight];
    },
    // En pantallas táctiles no se enfoca solo: abriría el teclado en pantalla y taparía media pantalla.
    focus: (el) => { if (el && !window.matchMedia("(hover: none) and (pointer: coarse)").matches) el.focus(); },
};

// Ctrl K / ⌘ K: ir al buscador de la barra superior.
document.addEventListener("keydown", e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        const input = document.querySelector(".top-search input");
        if (input) {
            e.preventDefault();
            input.focus();
            input.select();
        }
    }
});
