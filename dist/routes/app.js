"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.createAppRouter = createAppRouter;
const express_1 = require("express");
/** Placeholder pages for sections built in later increments, so the navigation has no dead links. */
const SECTIONS = [
    { path: '/search', title: 'Search', nav: '' },
];
function createAppRouter() {
    const router = (0, express_1.Router)();
    for (const s of SECTIONS) {
        router.get(s.path, (_req, res) => res.render('pages/coming-soon', { title: s.title, activeNav: s.nav }));
    }
    return router;
}
//# sourceMappingURL=app.js.map