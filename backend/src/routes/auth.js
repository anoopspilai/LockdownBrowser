import { login, logout, staffCookie, requireStaff, requireCsrf } from "../auth/staff.js";

export function registerAuthRoutes(router, ctx) {
  router.post("/api/v1/auth/login", async ({ req, res, body, ip }) => {
    requireCsrf(req);
    const { token, user } = await login(ctx, { username: body.username, password: body.password, ip });
    res.setHeader("Set-Cookie", staffCookie(ctx.config, token));
    return { ok: true, user };
  });

  router.post("/api/v1/auth/logout", ({ req, res, ip }) => {
    requireCsrf(req);
    try {
      const user = requireStaff(ctx, req);
      ctx.audit.staffAction(ctx, user, "auth.logout", { ip });
    } catch {
      /* already signed out */
    }
    logout(ctx, req);
    res.setHeader("Set-Cookie", staffCookie(ctx.config, "", { clear: true }));
    return { ok: true };
  });

  router.get("/api/v1/auth/me", ({ req }) => ({ user: requireStaff(ctx, req) }));
}
