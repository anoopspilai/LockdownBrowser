import { enroll } from "../auth/device.js";

export function registerDeviceRoutes(router, ctx) {
  router.post("/api/v1/devices/enroll", ({ body, ip }) => enroll(ctx, body, { ip }));
}
