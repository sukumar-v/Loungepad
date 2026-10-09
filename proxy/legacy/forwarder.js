/*
 * consolify-metadata, the metadata service's old name, kept only for the builds that have its
 * address baked in (consolify-metadata.s-varmagt.workers.dev, every build up to 1.8.0).
 *
 * Every request is handed, as it arrived, to loungepad-service through a service binding:
 * the same code, the same cache, the same rate limit, and the caller's own client address and
 * headers intact (a fetch to api.loungepad.app instead would arrive from this worker's address,
 * and the rate limit would count every old build as one caller). A service binding call is not
 * billed as a second request.
 *
 * Delete this worker once nobody runs a build from before 1.9.0:
 *   npx wrangler delete --name consolify-metadata
 */
export default {
  fetch(request, env) {
    return env.TARGET.fetch(request);
  },
};
