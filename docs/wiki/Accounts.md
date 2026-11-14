# Accounts

There are two completely separate kinds of accounts on UrsaNAS. Mixing them up is the most common source of confusion, so here's the distinction up front:

| | Who it's for | Where it's managed |
|---|---|---|
| **Administrators** | People who sign in to **this web interface** to manage the NAS. | **Administrators** page |
| **Users** | People who connect to a **shared folder** from their phone, laptop, etc. | **Users** page |

An administrator does not automatically get access to shares, and a share user cannot sign in to this interface — they're unrelated.

## Administrators

The **Administrators** page lists everyone who can sign in here. Add one for each person who should be able to manage the NAS, so you're not sharing a single password. Click **Password** next to an account to change it, or **Delete** to remove an administrator who no longer needs access (you can't delete the one you're currently signed in as).

## Users

The **Users** page lists the accounts people use to connect to your [shares](Sharing-Files). Click **Add a user**, choose a username and password, and tell the person those details — they'll be asked for them the first time they connect from their own device.

## Groups

If several users should always get the same access to the same shares, create a **Group** on the **Groups** page and add those users to it. You can then grant permissions to the whole group at once (in a share's settings, or in [Advanced Permissions](Advanced-Permissions)) instead of repeating the same setup for each person.
