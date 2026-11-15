import assert from 'node:assert/strict';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import { chromium } from 'playwright';

const internalToken = 'integration-internal-token-01234567890123456789';
const basic = 'Basic ' + Buffer.from('integration:integration-tests-only').toString('base64');
const customerId = randomUUID();
const otherId = randomUUID();
const code = randomBytes(32).toString('hex');
const otherCode = randomBytes(32).toString('hex');
let timestamp = Date.now();
const dashboardUid = id => 'egwn-' + createHash('sha256')
  .update(`${id.replaceAll('-', '')}:customer:${id.replaceAll('-', '')}`).digest('hex').slice(0, 32);

async function http(url, method = 'GET', body, authorization = basic) {
  const response = await fetch(url, {
    method,
    headers: { Authorization: authorization, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  return response;
}

async function internal(path, body) {
  return http(`http://api:8080/internal/registration/${path}`, 'POST', body, `Bearer ${internalToken}`);
}

async function keycloakAuthorization() {
  const response = await fetch('http://keycloak:8080/realms/egwn/protocol/openid-connect/token', {
    method: 'POST',
    body: new URLSearchParams({
      grant_type: 'client_credentials', client_id: 'egwn-provisioner', client_secret: 'integration-tests-only'
    })
  });
  assert.equal(response.status, 200);
  return 'Bearer ' + (await response.json()).access_token;
}

async function findUsers(username) {
  const response = await http(`http://keycloak:8080/admin/realms/egwn/users?exact=true&username=${encodeURIComponent(username)}`,
    'GET', undefined, await keycloakAuthorization());
  assert.equal(response.status, 200);
  return response.json();
}

async function eventually(check) {
  const deadline = Date.now() + 90000;
  while (Date.now() < deadline) {
    if (await check()) return;
    await new Promise(resolve => setTimeout(resolve, 300));
  }
  throw new Error('Timed out waiting for integration state.');
}

async function publish(id, registrationCode, deleted = false) {
  timestamp += 1000;
  const response = await http('http://rabbitmq:15672/api/exchanges/%2F/egwncentral.events/publish', 'POST', {
    properties: { delivery_mode: 2, content_type: 'application/json' },
    routing_key: 'customer.created.v1',
    payload_encoding: 'string',
    payload: JSON.stringify({
      eventId: randomUUID(), eventType: deleted ? 'customer.deleted' : 'customer.updated',
      schemaVersion: 1, occurredAtUtc: new Date(timestamp).toISOString(),
      customer: {
        id, customerNumber: `K-${id.slice(0, 8)}`, name: 'Browser integration',
        email: 'customer@example.invalid', isDeleted: deleted, registrationCode,
        meteringPoints: [], energyCommunities: []
      }
    })
  });
  assert.equal(response.status, 200);
  assert.equal((await response.json()).routed, true);
}

async function register(browser, registrationCode, expectedSuccess) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('http://grafana:3000/login/generic_oauth');
  assert.match(await page.locator('#kc-page-title').innerText(), /Anmelden|Konto/i);
  await page.locator('#kc-registration a').click();
  assert.match(await page.locator('#kc-page-title').innerText(), /Registrier|Konto erstellen/i);
  const username = `test-${randomUUID()}@example.invalid`;
  await page.locator('#firstName').fill('Integration');
  await page.locator('#lastName').fill('Customer');
  await page.locator('#email').fill(username);
  assert.equal(await page.locator('#username').count(), 0, 'Email is the username; no additional username field');
  const emailWidth = (await page.locator('#email').boundingBox()).width;
  for (const selector of ['#password', '#password-confirm', '#egwn_registration_code']) {
    const width = (await page.locator(selector).boundingBox()).width;
    assert.ok(Math.abs(width - emailWidth) < 2, `${selector} must have the same width as the email field`);
  }
  await page.locator('#password').fill('Integration-Test-Password-875029!');
  await page.locator('#password-confirm').fill('Integration-Test-Password-875029!');
  await page.locator('[name="egwn_registration_code"]').fill(registrationCode);
  await page.locator('#kc-register-form input[type="submit"]').click();
  if (expectedSuccess) {
    await page.waitForURL(url => url.hostname === 'grafana', { timeout: 45000 });
    const orgs = await page.request.get('http://grafana:3000/api/user/orgs');
    assert.equal(orgs.status(), 200);
    const memberships = await orgs.json();
    assert.equal(memberships.length, 1, 'No default or foreign organization access');
    assert.equal(memberships[0].role, 'Viewer');
    assert.equal(memberships[0].name, `egwn-${customerId.replaceAll('-', '')}`);
    const forbidden = await page.request.get(`http://grafana:3000/api/dashboards/uid/${dashboardUid(otherId)}`);
    assert.ok([403, 404].includes(forbidden.status()), 'Foreign customer dashboard must be inaccessible');
  } else {
    await page.locator('#egwn-code-error, #kc-error-message, .alert-error').first()
      .waitFor({ state: 'visible', timeout: 15000 });
    assert.equal(new URL(page.url()).hostname, 'keycloak');
  }
  return { context, page, username };
}

const browser = await chromium.launch({ headless: true });
const contexts = [];
try {
  await eventually(async () => (await http('http://api:8080/health/ready')).status === 200);
  await publish(customerId, code);
  await publish(otherId, otherCode);
  await eventually(async () => (await internal('resolve', { code })).status === 200 &&
    (await internal('resolve', { code: otherCode })).status === 200);
  const first = await register(browser, code, true);
  contexts.push(first.context);
  const second = await register(browser, code, true);
  contexts.push(second.context);
  const [firstUser] = await findUsers(first.username);
  assert.ok(firstUser?.id, 'Membership must use the actual Keycloak user ID');
  assert.equal((await internal('membership', { userId: firstUser.id })).status, 200);
  console.log('Two registrations with reusable code, Viewer mapping and cross-tenant denial passed.');

  const rotated = randomBytes(32).toString('hex');
  await publish(customerId, rotated);
  await eventually(async () => (await internal('resolve', { code })).status === 400 &&
    (await internal('resolve', { code: rotated })).status === 200);
  const invalid = await register(browser, code, false);
  contexts.push(invalid.context);
  assert.equal((await findUsers(invalid.username)).length, 0, 'Invalid code must not create an account');
  const preserved = await first.page.request.get('http://grafana:3000/api/user/orgs');
  assert.equal(preserved.status(), 200);
  assert.equal((await preserved.json()).length, 1);
  await first.context.clearCookies({ domain: 'grafana' });
  await first.page.goto('http://grafana:3000/login/generic_oauth');
  await first.page.waitForURL(url => url.hostname === 'grafana', { timeout: 45000 });
  assert.equal((await first.page.request.get('http://grafana:3000/api/user/orgs')).status(), 200);
  console.log('Code rotation blocks old code and preserves existing membership.');

  await publish(customerId, rotated, true);
  await eventually(async () => (await http(`http://grafana:3000/api/orgs/name/egwn-${customerId.replaceAll('-', '')}`)).status === 404);
  await eventually(async () => {
    const response = await first.page.request.get(`http://grafana:3000/api/dashboards/uid/${dashboardUid(customerId)}`);
    return [401, 403, 404].includes(response.status());
  });
  assert.equal((await internal('resolve', { code: rotated })).status, 400);
  await eventually(async () => (await internal('membership', { userId: firstUser.id })).status === 400);
  await eventually(async () => {
    const [user] = await findUsers(first.username);
    return user && !user.attributes?.egwn_org;
  });
  console.log('Customer deletion removes organization access and disables registration.');
} finally {
  // These are unique test customers; cleanup never touches unrelated organizations.
  await publish(customerId, randomBytes(32).toString('hex'), true);
  await publish(otherId, otherCode, true);
  for (const context of contexts) await context.close();
  await browser.close();
}
