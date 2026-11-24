// Development-only sign-in shortcuts for the accounts the API seeds in Development (see DemoDataSeeder).
//
// Production builds swap this file for demo-accounts.prod.ts (see "fileReplacements" in angular.json), so none of
// these credentials are compiled into the shipped bundle. CI checks that with `npm run check:bundle`.

export interface DemoAccount {
  label: string;
  email: string;
}

export const DEMO_PASSWORD = 'DemoPass!2026';

export const DEMO_ACCOUNTS: readonly DemoAccount[] = [
  { label: 'Acme admin', email: 'admin@acme.test' },
  { label: 'Acme agent', email: 'priya@acme.test' },
  { label: 'Acme requester', email: 'dana@acme.test' },
  { label: 'Globex admin', email: 'admin@globex.test' },
];
