# Alerts and Activity

UrsaNAS keeps an eye on itself in the background and lets you know when something needs your attention. This covers the **Alerts** and **Audit log** pages.

## Alerts

UrsaNAS regularly checks the health of your storage pools and disks. If something looks wrong — a pool that's degraded, a disk that's failing — it's raised as an **alert** on this page, marked **Warning** or **Critical**. An alert clears itself once the problem is resolved; past alerts stay visible under **History**.

Click **Check now** at any time for an immediate check instead of waiting for the next automatic one.

### Getting alerts by email

Open the **Email notifications** tab to have an email sent to you automatically whenever an alert is raised. Fill in your email provider's details (your provider's support pages will have the server address and port to use) and the addresses that should receive them, then click **Send a test email** to make sure it's working before you rely on it.

## Audit log

The **Audit log** (under Monitoring) is a plain history of actions taken on the NAS — shares created, settings changed, updates installed, and so on, each with who did it and when. Use it to answer "what changed, and when?" if something looks different from how you left it.
