/**
 * One mssql Transaction owns ONE connection and allows ONE request at a time (a second concurrent request fails with
 * EREQINPROG, and a rollback during a running request fails too, leaking the connection). Everything that issues SQL on a
 * transaction therefore goes through this per-transaction queue, so even code that fires queries concurrently
 * (Promise.all) runs them strictly one after another.
 */
const tails = new WeakMap<object, Promise<unknown>>();

export function serial<T>(tx: object, fn: () => Promise<T>): Promise<T> {
  const prev = tails.get(tx) ?? Promise.resolve();
  const run = prev.then(fn, fn); // a failed earlier query must not block later ones (they get their own outcome)
  tails.set(tx, run.catch(() => undefined));
  return run;
}
