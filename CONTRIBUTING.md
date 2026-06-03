# Contributing to CodeCleaners.PgProj

Thank you for using **CodeCleaners.PgProj**, a product of
**[CodeCleaners d.o.o.](https://code-cleaners.com/)**

This is proprietary software (see [`LICENSE`](./LICENSE)). It is **free to use**, but the source
code **may not be modified, copied, or redistributed without the Owner's direct written approval**.
That shapes how you can contribute:

## Bug reports & feature requests — always welcome

The most valuable contribution is telling us where the tool is wrong. Please
**[open an issue](https://github.com/ilijevicdenis/CodeCleaners.PgProj/issues)**.

A good report includes:

1. The **exact SQL** (a minimal repro) and the **PostgreSQL version** you target.
2. What you **expected** vs. what `pgproj` did — include the command you ran and its output
   (`build` / `validate` print the offending `file:line`).
3. If known, whether real PostgreSQL accepts or rejects it (the error message and `SQLSTATE`).

Parser/grammar gaps are especially useful: a statement PostgreSQL accepts but `pgproj` rejects (or
vice-versa). Each confirmed gap becomes a permanent test case so it can never regress.

## Code changes & pull requests

Because the license reserves modification and redistribution rights to the Owner, **unsolicited
pull requests cannot be merged without prior written approval**. If you'd like to contribute code:

1. **Open an issue first** to discuss the change, or contact CodeCleaners d.o.o. via
   https://code-cleaners.com/.
2. Proceed only once the Owner has approved the change in writing. Approved contributions are
   incorporated under, and remain subject to, the project [`LICENSE`](./LICENSE), with rights
   assigned to CodeCleaners d.o.o.

This keeps the codebase under a single clear owner while still letting the community shape the
product through issues and approved collaboration.

## Questions / permissions

For licensing questions, modification/redistribution requests, or commercial inquiries, contact
**CodeCleaners d.o.o.** — https://code-cleaners.com/
