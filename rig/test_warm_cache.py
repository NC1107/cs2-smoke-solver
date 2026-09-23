"""Tests for rig/warm-cache.py's retry loop.

A warm run is long enough to meet both of these in production: the solve rate
limiter refusing a re-warm that asks faster than a cold run ever does (429),
and watchtower replacing the container mid-list (connection refused). Either
used to end the run part-way through the map list.

Run: python3 rig/test_warm_cache.py
"""
import importlib.util
import pathlib
import unittest
import urllib.error
from unittest import mock

spec = importlib.util.spec_from_file_location("warm_cache", pathlib.Path(__file__).with_name("warm-cache.py"))
warm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(warm)

RESULT = [b'{"phase":"queued","count":0}\n', b'{"result":{"lineups":[1,2,3]}}\n']


def refused(code=429, retry_after=None):
    headers = {"Retry-After": retry_after} if retry_after else {}
    return urllib.error.HTTPError("http://x/api/lineup", code, "refused", headers, None)


class Retry(unittest.TestCase):
    def solve_with(self, *outcomes):
        """Run solve() against a server that answers with `outcomes` in turn."""
        calls = iter(outcomes)

        def urlopen(req, timeout):
            outcome = next(calls)
            if isinstance(outcome, Exception):
                raise outcome
            return outcome

        with mock.patch.object(warm.urllib.request, "urlopen", urlopen), \
                mock.patch.object(warm.time, "sleep") as sleep:
            return warm.solve("de_test", [0, 0, 0]), sleep

    def test_waits_out_a_rate_limit(self):
        (_, lineups), sleep = self.solve_with(refused(), refused(), RESULT)
        self.assertEqual(lineups, 3)
        self.assertEqual(sleep.call_count, 2)

    def test_honours_retry_after(self):
        _, sleep = self.solve_with(refused(retry_after="7"), RESULT)
        sleep.assert_called_once_with(7.0)

    def test_rides_out_a_container_swap(self):
        (_, lineups), sleep = self.solve_with(urllib.error.URLError("connection refused"), RESULT)
        self.assertEqual(lineups, 3)
        self.assertEqual(sleep.call_count, 1)

    def test_a_real_error_is_not_retried(self):
        with self.assertRaises(urllib.error.HTTPError):
            self.solve_with(refused(code=500), RESULT)

    def test_gives_up_after_a_minute_of_refusals(self):
        with self.assertRaises(urllib.error.HTTPError):
            self.solve_with(*[refused() for _ in range(8)])


if __name__ == "__main__":
    unittest.main()
