namespace Dispatch.Infrastructure.Scripting;

/// <summary>JavaScript that builds the Postman-compatible <c>pm</c> object on top of a few host functions.</summary>
internal static class ScriptPrelude
{
    public const string Source = """
        (function (g) {
          'use strict';

          function fmt(v) {
            if (typeof v === 'string') return JSON.stringify(v);
            if (v === undefined) return 'undefined';
            try { return JSON.stringify(v); } catch (e) { return String(v); }
          }

          function deepEqual(a, b) {
            if (a === b) return true;
            if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') return a !== a && b !== b;
            if (Array.isArray(a) !== Array.isArray(b)) return false;
            var ka = Object.keys(a), kb = Object.keys(b);
            if (ka.length !== kb.length) return false;
            for (var i = 0; i < ka.length; i++) if (!deepEqual(a[ka[i]], b[ka[i]])) return false;
            return true;
          }

          function typeOf(v) {
            if (v === null) return 'null';
            if (Array.isArray(v)) return 'array';
            if (v instanceof RegExp) return 'regexp';
            return typeof v;
          }

          function AssertionError(message) { this.name = 'AssertionError'; this.message = message; }
          AssertionError.prototype = Object.create(Error.prototype);

          // A chai-style assertion chain covering the commonly used BDD vocabulary.
          function Assertion(actual, flags) {
            this._actual = actual;
            this._flags = flags || {};
          }
          function chain(name) {
            Object.defineProperty(Assertion.prototype, name, { get: function () { return this; } });
          }
          ['to', 'be', 'been', 'is', 'that', 'which', 'and', 'has', 'have', 'with', 'at', 'of', 'same', 'does', 'still', 'also', 'but']
            .forEach(chain);
          Object.defineProperty(Assertion.prototype, 'not', { get: function () { this._flags.negate = !this._flags.negate; return this; } });
          Object.defineProperty(Assertion.prototype, 'deep', { get: function () { this._flags.deep = true; return this; } });
          Object.defineProperty(Assertion.prototype, 'nested', { get: function () { this._flags.nested = true; return this; } });

          Assertion.prototype._assert = function (ok, message, negatedMessage) {
            if (this._flags.negate) ok = !ok;
            if (!ok) throw new AssertionError(this._flags.negate ? negatedMessage : message);
            return this;
          };

          function flagGetter(name, test, message, negated) {
            Object.defineProperty(Assertion.prototype, name, {
              get: function () { return this._assert(test(this._actual), message(this._actual), negated(this._actual)); }
            });
          }
          flagGetter('ok', function (v) { return !!v; }, function (v) { return 'expected ' + fmt(v) + ' to be truthy'; }, function (v) { return 'expected ' + fmt(v) + ' to be falsy'; });
          flagGetter('true', function (v) { return v === true; }, function (v) { return 'expected ' + fmt(v) + ' to be true'; }, function (v) { return 'expected ' + fmt(v) + ' not to be true'; });
          flagGetter('false', function (v) { return v === false; }, function (v) { return 'expected ' + fmt(v) + ' to be false'; }, function (v) { return 'expected ' + fmt(v) + ' not to be false'; });
          flagGetter('null', function (v) { return v === null; }, function (v) { return 'expected ' + fmt(v) + ' to be null'; }, function (v) { return 'expected value not to be null'; });
          flagGetter('undefined', function (v) { return v === undefined; }, function (v) { return 'expected ' + fmt(v) + ' to be undefined'; }, function (v) { return 'expected value not to be undefined'; });
          flagGetter('exist', function (v) { return v !== null && v !== undefined; }, function (v) { return 'expected value to exist'; }, function (v) { return 'expected ' + fmt(v) + ' not to exist'; });
          flagGetter('empty', function (v) {
            if (typeof v === 'string' || Array.isArray(v)) return v.length === 0;
            if (v && typeof v === 'object') return Object.keys(v).length === 0;
            return false;
          }, function (v) { return 'expected ' + fmt(v) + ' to be empty'; }, function (v) { return 'expected ' + fmt(v) + ' not to be empty'; });

          Assertion.prototype.equal = Assertion.prototype.equals = Assertion.prototype.eq = function (expected) {
            var ok = this._flags.deep ? deepEqual(this._actual, expected) : this._actual === expected;
            return this._assert(ok, 'expected ' + fmt(this._actual) + ' to equal ' + fmt(expected),
              'expected ' + fmt(this._actual) + ' not to equal ' + fmt(expected));
          };
          Assertion.prototype.eql = function (expected) {
            return this._assert(deepEqual(this._actual, expected), 'expected ' + fmt(this._actual) + ' to deeply equal ' + fmt(expected),
              'expected ' + fmt(this._actual) + ' not to deeply equal ' + fmt(expected));
          };
          Assertion.prototype.a = Assertion.prototype.an = function (type) {
            var t = typeOf(this._actual);
            return this._assert(t === String(type).toLowerCase(), 'expected ' + fmt(this._actual) + ' to be a ' + type + ' but got ' + t,
              'expected ' + fmt(this._actual) + ' not to be a ' + type);
          };
          Assertion.prototype.include = Assertion.prototype.contain = Assertion.prototype.includes = Assertion.prototype.contains = function (item) {
            var a = this._actual, ok;
            if (typeof a === 'string') ok = a.indexOf(item) >= 0;
            else if (Array.isArray(a)) ok = a.some(function (x) { return deepEqual(x, item); });
            else if (a && typeof a === 'object' && item && typeof item === 'object') ok = Object.keys(item).every(function (k) { return deepEqual(a[k], item[k]); });
            else ok = false;
            return this._assert(ok, 'expected ' + fmt(a) + ' to include ' + fmt(item), 'expected ' + fmt(a) + ' not to include ' + fmt(item));
          };
          function compare(name, test, word) {
            Assertion.prototype[name] = function (n) {
              return this._assert(test(this._actual, n), 'expected ' + fmt(this._actual) + ' to be ' + word + ' ' + n,
                'expected ' + fmt(this._actual) + ' not to be ' + word + ' ' + n);
            };
          }
          compare('above', function (a, n) { return a > n; }, 'above');
          compare('gt', function (a, n) { return a > n; }, 'above');
          compare('greaterThan', function (a, n) { return a > n; }, 'above');
          compare('least', function (a, n) { return a >= n; }, 'at least');
          compare('gte', function (a, n) { return a >= n; }, 'at least');
          compare('below', function (a, n) { return a < n; }, 'below');
          compare('lt', function (a, n) { return a < n; }, 'below');
          compare('lessThan', function (a, n) { return a < n; }, 'below');
          compare('most', function (a, n) { return a <= n; }, 'at most');
          compare('lte', function (a, n) { return a <= n; }, 'at most');
          Assertion.prototype.within = function (lo, hi) {
            var a = this._actual;
            return this._assert(a >= lo && a <= hi, 'expected ' + fmt(a) + ' to be within ' + lo + '..' + hi,
              'expected ' + fmt(a) + ' not to be within ' + lo + '..' + hi);
          };
          Assertion.prototype.oneOf = function (list) {
            var a = this._actual;
            return this._assert(list.some(function (x) { return deepEqual(x, a); }), 'expected ' + fmt(a) + ' to be one of ' + fmt(list),
              'expected ' + fmt(a) + ' not to be one of ' + fmt(list));
          };
          Assertion.prototype.match = Assertion.prototype.matches = function (re) {
            return this._assert(re.test(String(this._actual)), 'expected ' + fmt(this._actual) + ' to match ' + re,
              'expected ' + fmt(this._actual) + ' not to match ' + re);
          };
          Assertion.prototype.lengthOf = Assertion.prototype.length = function (n) {
            var len = this._actual == null ? undefined : this._actual.length;
            return this._assert(len === n, 'expected ' + fmt(this._actual) + ' to have length ' + n + ' but got ' + len,
              'expected ' + fmt(this._actual) + ' not to have length ' + n);
          };
          Assertion.prototype.property = function (name, value) {
            var obj = this._actual, has, actual;
            if (this._flags.nested) {
              actual = String(name).split('.').reduce(function (o, k) { return o == null ? undefined : o[k]; }, obj);
              has = actual !== undefined;
            } else {
              has = obj != null && Object.prototype.hasOwnProperty.call(Object(obj), name);
              actual = has ? obj[name] : undefined;
            }
            if (arguments.length < 2)
              return this._assert(has, 'expected ' + fmt(obj) + ' to have property ' + fmt(name), 'expected ' + fmt(obj) + ' not to have property ' + fmt(name));
            return this._assert(has && deepEqual(actual, value), 'expected property ' + fmt(name) + ' to be ' + fmt(value) + ' but got ' + fmt(actual),
              'expected property ' + fmt(name) + ' not to be ' + fmt(value));
          };
          Assertion.prototype.keys = Assertion.prototype.key = function () {
            var keys = Array.prototype.concat.apply([], arguments), obj = this._actual || {};
            var ok = keys.every(function (k) { return Object.prototype.hasOwnProperty.call(obj, k); });
            return this._assert(ok, 'expected ' + fmt(obj) + ' to have keys ' + fmt(keys), 'expected ' + fmt(obj) + ' not to have keys ' + fmt(keys));
          };
          Assertion.prototype.members = function (list) {
            var a = this._actual || [];
            var ok = a.length === list.length && list.every(function (x) { return a.some(function (y) { return deepEqual(x, y); }); });
            return this._assert(ok, 'expected ' + fmt(a) + ' to have the same members as ' + fmt(list), 'expected ' + fmt(a) + ' not to have the same members as ' + fmt(list));
          };
          Assertion.prototype.instanceof = Assertion.prototype.instanceOf = function (ctor) {
            return this._assert(this._actual instanceof ctor, 'expected value to be an instance of ' + (ctor && ctor.name), 'expected value not to be an instance of ' + (ctor && ctor.name));
          };
          Assertion.prototype.throw = function () {
            var threw = false;
            try { this._actual(); } catch (e) { threw = true; }
            return this._assert(threw, 'expected function to throw', 'expected function not to throw');
          };
          // Response-specific: pm.response.to.have.status(200), .header('X'), .jsonBody('a.b'), .be.ok
          Assertion.prototype.status = function (code) {
            var r = this._actual;
            var ok = typeof code === 'number' ? r.code === code : r.status === code;
            return this._assert(ok, 'expected response to have status ' + code + ' but got ' + r.code,
              'expected response not to have status ' + code);
          };
          Assertion.prototype.header = function (name, value) {
            var r = this._actual, actual = r.headers.get(name);
            var ok = arguments.length < 2 ? actual !== undefined : actual === value;
            return this._assert(ok, 'expected response to have header ' + name + (arguments.length < 2 ? '' : ' = ' + fmt(value)) + ' but got ' + fmt(actual),
              'expected response not to have header ' + name);
          };
          Assertion.prototype.body = function (text) {
            var r = this._actual;
            return this._assert(r.text() === text, 'expected response body to equal ' + fmt(text), 'expected response body not to equal ' + fmt(text));
          };
          Assertion.prototype.jsonBody = function (path, value) {
            var r = this._actual, json;
            try { json = r.json(); } catch (e) { return this._assert(false, 'expected response body to be JSON', ''); }
            if (arguments.length === 0) return this._assert(true, '', 'expected response body not to be JSON');
            var actual = String(path).split('.').reduce(function (o, k) { return o == null ? undefined : o[k]; }, json);
            if (arguments.length < 2) return this._assert(actual !== undefined, 'expected JSON body to have ' + path, 'expected JSON body not to have ' + path);
            return this._assert(deepEqual(actual, value), 'expected ' + path + ' to be ' + fmt(value) + ' but got ' + fmt(actual), 'expected ' + path + ' not to be ' + fmt(value));
          };

          var expect = function (actual, message) { var a = new Assertion(actual, {}); a._message = message; return a; };
          expect.fail = function (message) { throw new AssertionError(message || 'expect.fail()'); };

          function scope(getter, setter, unsetter) {
            return {
              get: function (k) { var v = getter(String(k)); return v === null ? undefined : v; },
              set: function (k, v) { setter(String(k), typeof v === 'string' ? v : JSON.stringify(v)); },
              has: function (k) { return getter(String(k)) !== null; },
              unset: function (k) { if (unsetter) unsetter(String(k)); },
              replaceIn: function (text) { return __host.resolve(String(text)); },
              toObject: function () { return JSON.parse(__host.variablesJson()); }
            };
          }

          function headerList(list) {
            return {
              _list: list,
              get: function (name) {
                var n = String(name).toLowerCase();
                for (var i = 0; i < list.length; i++) if (String(list[i].key).toLowerCase() === n) return list[i].value;
                return undefined;
              },
              has: function (name) { return this.get(name) !== undefined; },
              add: function (h) { list.push({ key: h.key, value: String(h.value) }); },
              upsert: function (h) {
                var n = String(h.key).toLowerCase();
                for (var i = 0; i < list.length; i++) if (String(list[i].key).toLowerCase() === n) { list[i].value = String(h.value); return; }
                list.push({ key: h.key, value: String(h.value) });
              },
              remove: function (name) {
                var n = String(name).toLowerCase();
                for (var i = list.length - 1; i >= 0; i--) if (String(list[i].key).toLowerCase() === n) list.splice(i, 1);
              },
              toObject: function () { var o = {}; list.forEach(function (h) { o[h.key] = h.value; }); return o; },
              each: function (fn) { list.forEach(fn); },
              all: function () { return list.slice(); }
            };
          }

          var req = JSON.parse(__host.requestJson());
          var request = {
            url: req.url,
            method: req.method,
            headers: headerList(req.headers),
            body: { mode: req.bodyMode, raw: req.body },
            name: req.name
          };

          var response;
          var responseJson = __host.responseJson();
          if (responseJson) {
            var r = JSON.parse(responseJson);
            var parsed;
            response = {
              code: r.code,
              status: r.status,
              responseTime: r.responseTime,
              responseSize: r.responseSize,
              headers: headerList(r.headers),
              messages: r.messages,
              text: function () { return r.body; },
              json: function () { if (parsed === undefined) parsed = JSON.parse(r.body); return parsed; }
            };
            response.to = {
              get have() { return new Assertion(response, {}); },
              get be() {
                var a = new Assertion(response, {});
                Object.defineProperty(a, 'ok', { get: function () { return a._assert(r.code >= 200 && r.code < 300, 'expected response to be 2xx but got ' + r.code, 'expected response not to be 2xx'); } });
                Object.defineProperty(a, 'success', { get: function () { return a.ok; } });
                Object.defineProperty(a, 'error', { get: function () { return a._assert(r.code >= 400, 'expected an error response but got ' + r.code, 'expected a non-error response'); } });
                Object.defineProperty(a, 'serverError', { get: function () { return a._assert(r.code >= 500, 'expected 5xx but got ' + r.code, 'expected not 5xx'); } });
                Object.defineProperty(a, 'clientError', { get: function () { return a._assert(r.code >= 400 && r.code < 500, 'expected 4xx but got ' + r.code, 'expected not 4xx'); } });
                return a;
              },
              get not() { var a = new Assertion(response, { negate: true }); return { have: a, be: a }; }
            };
          }

          var pm = {
            environment: scope(__host.envGet, __host.envSet, __host.envUnset),
            variables: scope(__host.varGet, __host.varSet, __host.varUnset),
            collectionVariables: scope(__host.varGet, __host.varSet, __host.varUnset),
            globals: scope(__host.globalGet, __host.globalSet, null),
            iterationData: scope(__host.dataGet, function () {}, null),
            request: request,
            response: response,
            info: { requestName: req.name, iteration: __host.iteration(), eventName: response ? 'test' : 'prerequest' },
            expect: expect,
            test: function (name, fn) {
              try {
                fn();
                __host.test(String(name), true, '');
              } catch (e) {
                __host.test(String(name), false, e && e.message ? e.message : String(e));
              }
            }
          };
          pm.visualizer = {
            set: function (template, data) {
              __host.visualize(String(template), JSON.stringify(data === undefined ? {} : data));
            },
            clear: function () {}
          };
          pm.test.skip = function (name) { __host.test(String(name) + ' (skipped)', true, ''); };

          function log(level) {
            return function () {
              var parts = [];
              for (var i = 0; i < arguments.length; i++) parts.push(typeof arguments[i] === 'string' ? arguments[i] : fmt(arguments[i]));
              __host.log(level, parts.join(' '));
            };
          }

          g.pm = pm;
          g.dispatch = pm;
          g.expect = expect;
          g.console = { log: log('log'), info: log('info'), warn: log('warn'), error: log('error'), debug: log('debug') };
          g.btoa = function (s) { return __host.btoa(String(s)); };
          g.atob = function (s) { return __host.atob(String(s)); };
          g.__exportRequest = function () {
            return JSON.stringify({ url: String(request.url), method: String(request.method), headers: request.headers._list,
              body: request.body.raw == null ? null : String(request.body.raw) });
          };
        })(this);
        """;
}
