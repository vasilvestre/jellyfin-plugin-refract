// Minimal QR encoder: byte mode only, error correction level M, SVG output.
// Replaces the 2297-line vendored Kazuhiko Arase library. The widget only ever
// encodes the short device-code verification URL as bytes at level M, so the
// numeric, alphanumeric and kanji modes and the table, image, GIF and base64
// renderers are gone. The encoding follows ISO/IEC 18004, so the output scans
// exactly like the original.
(function () {
    'use strict';

    // Reed-Solomon over GF(256), primitive polynomial 0x11D.
    var GF_EXP = [];
    var GF_LOG = [];
    (function () {
        var x = 1;
        for (var i = 0; i < 255; i++) {
            GF_EXP[i] = x;
            GF_LOG[x] = i;
            x <<= 1;
            if (x & 0x100) x ^= 0x11D;
        }
        for (var j = 255; j < 512; j++) GF_EXP[j] = GF_EXP[j - 255];
    }());

    function gfMul(x, y) {
        if (x === 0 || y === 0) return 0;
        return GF_EXP[GF_LOG[x] + GF_LOG[y]];
    }

    function rsDivisor(degree) {
        var result = [];
        for (var i = 0; i < degree - 1; i++) result.push(0);
        result.push(1);
        var root = 1;
        for (var r = 0; r < degree; r++) {
            for (var j = 0; j < result.length; j++) {
                result[j] = gfMul(result[j], root);
                if (j + 1 < result.length) result[j] ^= result[j + 1];
            }
            root = gfMul(root, 0x02);
        }
        return result;
    }

    function rsRemainder(data, divisor) {
        var result = divisor.map(function () { return 0; });
        for (var b = 0; b < data.length; b++) {
            var factor = data[b] ^ result.shift();
            result.push(0);
            for (var i = 0; i < divisor.length; i++) {
                result[i] ^= gfMul(divisor[i], factor);
            }
        }
        return result;
    }

    // EC codewords per block and block counts for level M, versions 1..40. Index 0 unused.
    var ECC_PER_BLOCK = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
        26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28];
    var NUM_BLOCKS = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
        17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49];

    function rawDataModules(ver) {
        var result = (16 * ver + 128) * ver + 64;
        if (ver >= 2) {
            var numAlign = Math.floor(ver / 7) + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    function dataCodewords(ver) {
        return Math.floor(rawDataModules(ver) / 8) - ECC_PER_BLOCK[ver] * NUM_BLOCKS[ver];
    }

    function alignmentPositions(ver) {
        if (ver === 1) return [];
        var numAlign = Math.floor(ver / 7) + 2;
        var step = (ver === 32) ? 26 : Math.floor((ver * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2)) * 2;
        var result = [6];
        for (var pos = ver * 4 + 10; result.length < numAlign; pos -= step) {
            result.splice(1, 0, pos);
        }
        return result;
    }

    function bit(x, i) { return ((x >>> i) & 1) !== 0; }

    function toUtf8(str) {
        var bytes = [];
        for (var i = 0; i < str.length; i++) {
            var c = str.charCodeAt(i);
            if (c < 0x80) bytes.push(c);
            else if (c < 0x800) bytes.push(0xC0 | (c >> 6), 0x80 | (c & 0x3F));
            else if (c < 0xD800 || c >= 0xE000) bytes.push(0xE0 | (c >> 12), 0x80 | ((c >> 6) & 0x3F), 0x80 | (c & 0x3F));
            else {
                c = 0x10000 + (((c & 0x3FF) << 10) | (str.charCodeAt(++i) & 0x3FF));
                bytes.push(0xF0 | (c >> 18), 0x80 | ((c >> 12) & 0x3F), 0x80 | ((c >> 6) & 0x3F), 0x80 | (c & 0x3F));
            }
        }
        return bytes;
    }

    function qrcode(typeNumber) {
        // The widget always asks for level M; the level argument is accepted for
        // API compatibility and ignored.
        var version = typeNumber || 0;
        var dataBytes = [];
        var moduleCount = 0;
        var modules = [];
        var isFunction = [];

        function setFunction(x, y, dark) {
            modules[y][x] = dark;
            isFunction[y][x] = true;
        }

        function drawFinder(x, y) {
            for (var dy = -4; dy <= 4; dy++) {
                for (var dx = -4; dx <= 4; dx++) {
                    var dist = Math.max(Math.abs(dx), Math.abs(dy));
                    var xx = x + dx;
                    var yy = y + dy;
                    if (xx >= 0 && xx < moduleCount && yy >= 0 && yy < moduleCount) {
                        setFunction(xx, yy, dist !== 2 && dist !== 4);
                    }
                }
            }
        }

        function drawAlignment(x, y) {
            for (var dy = -2; dy <= 2; dy++) {
                for (var dx = -2; dx <= 2; dx++) {
                    setFunction(x + dx, y + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
                }
            }
        }

        function drawFormat(mask) {
            // EC level M has format bits 00, then the 3 mask bits, BCH, XOR 0x5412.
            var data = mask;
            var rem = data;
            for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
            var bits = ((data << 10) | rem) ^ 0x5412;
            for (var a = 0; a <= 5; a++) setFunction(8, a, bit(bits, a));
            setFunction(8, 7, bit(bits, 6));
            setFunction(8, 8, bit(bits, 7));
            setFunction(7, 8, bit(bits, 8));
            for (var b = 9; b < 15; b++) setFunction(14 - b, 8, bit(bits, b));
            for (var c = 0; c < 8; c++) setFunction(moduleCount - 1 - c, 8, bit(bits, c));
            for (var d = 8; d < 15; d++) setFunction(8, moduleCount - 15 + d, bit(bits, d));
            setFunction(8, moduleCount - 8, true);
        }

        function drawVersion() {
            var rem = version;
            for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1F25);
            var bits = (version << 12) | rem;
            for (var i = 0; i < 18; i++) {
                var dark = bit(bits, i);
                var a = moduleCount - 11 + (i % 3);
                var b = Math.floor(i / 3);
                setFunction(a, b, dark);
                setFunction(b, a, dark);
            }
        }

        function drawFunctionPatterns() {
            for (var i = 0; i < moduleCount; i++) {
                setFunction(6, i, i % 2 === 0);
                setFunction(i, 6, i % 2 === 0);
            }
            drawFinder(3, 3);
            drawFinder(moduleCount - 4, 3);
            drawFinder(3, moduleCount - 4);
            var positions = alignmentPositions(version);
            var n = positions.length;
            for (var i = 0; i < n; i++) {
                for (var j = 0; j < n; j++) {
                    if (!(i === 0 && j === 0 || i === 0 && j === n - 1 || i === n - 1 && j === 0)) {
                        drawAlignment(positions[i], positions[j]);
                    }
                }
            }
            drawFormat(0);
            if (version >= 7) drawVersion();
        }

        function applyMask(mask) {
            for (var y = 0; y < moduleCount; y++) {
                for (var x = 0; x < moduleCount; x++) {
                    var invert;
                    switch (mask) {
                        case 0: invert = (x + y) % 2 === 0; break;
                        case 1: invert = y % 2 === 0; break;
                        case 2: invert = x % 3 === 0; break;
                        case 3: invert = (x + y) % 3 === 0; break;
                        case 4: invert = (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0; break;
                        case 5: invert = (x * y) % 2 + (x * y) % 3 === 0; break;
                        case 6: invert = ((x * y) % 2 + (x * y) % 3) % 2 === 0; break;
                        case 7: invert = ((x + y) % 2 + (x * y) % 3) % 2 === 0; break;
                    }
                    modules[y][x] = modules[y][x] !== (invert && !isFunction[y][x]);
                }
            }
        }

        function addHistory(runLength, history) {
            if (history[0] === 0) runLength += moduleCount; // light border on the first run
            history.pop();
            history.unshift(runLength);
        }

        function countPatterns(history) {
            var n = history[1];
            var core = n > 0 && history[2] === n && history[3] === n * 3 && history[4] === n && history[5] === n;
            return (core && history[0] >= n * 4 && history[6] >= n) ? 1 : 0;
        }

        function terminateAndCount(currentRunColor, runLength, history) {
            if (currentRunColor) {
                addHistory(runLength, history);
                runLength = 0;
            }
            runLength += moduleCount;
            addHistory(runLength, history);
            return countPatterns(history);
        }

        function penalty() {
            var result = 0;
            var y, x, run, color, history;
            for (y = 0; y < moduleCount; y++) {
                color = false;
                run = 0;
                history = [0, 0, 0, 0, 0, 0, 0];
                for (x = 0; x < moduleCount; x++) {
                    if (modules[y][x] === color) {
                        run++;
                        if (run === 5) result += 3;
                        else if (run > 5) result++;
                    } else {
                        addHistory(run, history);
                        if (!color) result += countPatterns(history) * 40;
                        color = modules[y][x];
                        run = 1;
                    }
                }
                result += terminateAndCount(color, run, history) * 40;
            }
            for (x = 0; x < moduleCount; x++) {
                color = false;
                run = 0;
                history = [0, 0, 0, 0, 0, 0, 0];
                for (y = 0; y < moduleCount; y++) {
                    if (modules[y][x] === color) {
                        run++;
                        if (run === 5) result += 3;
                        else if (run > 5) result++;
                    } else {
                        addHistory(run, history);
                        if (!color) result += countPatterns(history) * 40;
                        color = modules[y][x];
                        run = 1;
                    }
                }
                result += terminateAndCount(color, run, history) * 40;
            }
            for (y = 0; y < moduleCount - 1; y++) {
                for (x = 0; x < moduleCount - 1; x++) {
                    var c = modules[y][x];
                    if (c === modules[y][x + 1] && c === modules[y + 1][x] && c === modules[y + 1][x + 1]) {
                        result += 3;
                    }
                }
            }
            var dark = 0;
            for (y = 0; y < moduleCount; y++) {
                for (x = 0; x < moduleCount; x++) {
                    if (modules[y][x]) dark++;
                }
            }
            var total = moduleCount * moduleCount;
            var k = Math.ceil(Math.abs(dark * 20 - total * 10) / total) - 1;
            result += k * 10;
            return result;
        }

        function autoVersion() {
            for (var v = 1; v <= 40; v++) {
                var usedBits = 4 + (v <= 9 ? 8 : 16) + 8 * dataBytes.length;
                if (usedBits <= dataCodewords(v) * 8) return v;
            }
            throw new Error('Data too long for a QR code');
        }

        function buildCodewords(ver) {
            var capacityBits = dataCodewords(ver) * 8;
            var buffer = [];
            var bitLength = 0;
            function put(num, len) {
                for (var i = len - 1; i >= 0; i--) {
                    var idx = Math.floor(bitLength / 8);
                    if (buffer.length <= idx) buffer.push(0);
                    if (((num >>> i) & 1) === 1) buffer[idx] |= (0x80 >>> (bitLength % 8));
                    bitLength++;
                }
            }
            put(4, 4); // byte mode indicator
            put(dataBytes.length, ver <= 9 ? 8 : 16);
            for (var i = 0; i < dataBytes.length; i++) put(dataBytes[i], 8);
            put(0, Math.min(4, capacityBits - bitLength)); // terminator
            put(0, (8 - (bitLength % 8)) % 8); // pad to a byte boundary
            for (var pad = 0xEC; bitLength < capacityBits; pad ^= 0xEC ^ 0x11) put(pad, 8);

            var data = buffer.slice(0, dataCodewords(ver));
            return addEccAndInterleave(data, ver);
        }

        function addEccAndInterleave(data, ver) {
            var numBlocks = NUM_BLOCKS[ver];
            var blockEccLen = ECC_PER_BLOCK[ver];
            var rawCodewords = Math.floor(rawDataModules(ver) / 8);
            var numShortBlocks = numBlocks - (rawCodewords % numBlocks);
            var shortBlockLen = Math.floor(rawCodewords / numBlocks);
            var divisor = rsDivisor(blockEccLen);
            var blocks = [];
            for (var i = 0, k = 0; i < numBlocks; i++) {
                var dat = data.slice(k, k + shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1));
                k += dat.length;
                var ecc = rsRemainder(dat, divisor);
                if (i < numShortBlocks) dat.push(0);
                blocks.push({ dat: dat, ecc: ecc });
            }
            var result = [];
            for (var i = 0; i < blocks[0].dat.length; i++) {
                for (var j = 0; j < blocks.length; j++) {
                    if (i !== shortBlockLen - blockEccLen || j >= numShortBlocks) {
                        result.push(blocks[j].dat[i]);
                    }
                }
            }
            for (var i = 0; i < blockEccLen; i++) {
                for (var j = 0; j < blocks.length; j++) {
                    result.push(blocks[j].ecc[i]);
                }
            }
            return result;
        }

        function drawCodewords(data) {
            var i = 0;
            for (var right = moduleCount - 1; right >= 1; right -= 2) {
                if (right === 6) right = 5;
                for (var vert = 0; vert < moduleCount; vert++) {
                    for (var j = 0; j < 2; j++) {
                        var x = right - j;
                        var upward = ((right + 1) & 2) === 0;
                        var y = upward ? moduleCount - 1 - vert : vert;
                        if (!isFunction[y][x] && i < data.length * 8) {
                            modules[y][x] = bit(data[i >>> 3], 7 - (i & 7));
                            i++;
                        }
                    }
                }
            }
        }

        function make() {
            var ver = version || autoVersion();
            version = ver;
            moduleCount = ver * 4 + 17;
            modules = [];
            isFunction = [];
            for (var i = 0; i < moduleCount; i++) {
                modules.push([]);
                isFunction.push([]);
                for (var j = 0; j < moduleCount; j++) {
                    modules[i].push(false);
                    isFunction[i].push(false);
                }
            }
            var codewords = buildCodewords(ver);
            drawFunctionPatterns();
            drawCodewords(codewords);

            var bestMask = 0;
            var bestPenalty = Infinity;
            for (var mask = 0; mask < 8; mask++) {
                applyMask(mask);
                drawFormat(mask);
                var p = penalty();
                if (p < bestPenalty) {
                    bestPenalty = p;
                    bestMask = mask;
                }
                applyMask(mask);
            }
            applyMask(bestMask);
            drawFormat(bestMask);
        }

        return {
            addData: function (data) {
                dataBytes = dataBytes.concat(toUtf8(data));
            },
            make: make,
            getModuleCount: function () { return moduleCount; },
            isDark: function (row, col) {
                if (row < 0 || moduleCount <= row || col < 0 || moduleCount <= col) {
                    throw new Error(row + ',' + col);
                }
                return modules[row][col];
            },
            createSvgTag: function (opts) {
                opts = opts || {};
                var cellSize = opts.cellSize || 2;
                var margin = (typeof opts.margin === 'undefined') ? cellSize * 4 : opts.margin;
                var size = moduleCount * cellSize + margin * 2;
                var path = '';
                for (var r = 0; r < moduleCount; r++) {
                    for (var c = 0; c < moduleCount; c++) {
                        if (modules[r][c]) {
                            path += 'M' + (c * cellSize + margin) + ',' + (r * cellSize + margin) +
                                'l' + cellSize + ',0 0,' + cellSize + ' -' + cellSize + ',0 0,-' + cellSize + 'z ';
                        }
                    }
                }
                return '<svg xmlns="http://www.w3.org/2000/svg" width="' + size + '" height="' + size + '" viewBox="0 0 ' + size + ' ' + size + '" shape-rendering="crispEdges">' +
                    '<rect width="100%" height="100%" fill="white"/>' +
                    '<path d="' + path + '" fill="black"/></svg>';
            }
        };
    }

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = qrcode;
    }
    if (typeof window !== 'undefined') {
        window.qrcode = qrcode;
    }
}());
