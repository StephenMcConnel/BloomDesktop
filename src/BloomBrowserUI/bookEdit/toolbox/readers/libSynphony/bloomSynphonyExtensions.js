/**
 * Extensions to the LibSynphony class to support Bloom.
 */
import XRegExp from "xregexp";
require("./bloom_xregexp_categories.js"); // reviewslog should add PEP to XRegExp, but it's not working
import { theOneLibSynphony, LanguageData, LibSynphony } from "./synphony_lib";
import * as _ from "underscore";

export function clearWordCache() {
    theOneWordCache = null;
}

var theOneWordCache;

/**
 * Grapheme data in LanguageData.GPCS
 * @param {String} optionalGrapheme Optional. The grapheme to initialize the class.
 * @returns {DataGPC}
 */
function DataGPC(optionalGrapheme) {
    var s = typeof optionalGrapheme === "undefined" ? "" : optionalGrapheme;

    this.GPC = s;
    this.GPCuc = s.toUpperCase();
    this.Grapheme = s;
    this.Phoneme = s;
    this.Category = "other";
    this.Combining = "false";
    this.Frequency = 1;
    this.TokenFreq = 1;
    this.IPA = "";
    this.Alt = [];
}

/**
 * Word data in LanguageData.geoup1
 * @param {String} optionalWord Optional. The word to initialize the class.
 * @returns {DataWord}
 */
export function DataWord(optionalWord) {
    var w = typeof optionalWord === "undefined" ? "" : optionalWord;

    this.Name = w;
    this.Count = 1;
    this.Group = 1;
    this.PartOfSpeech = "";
    this.GPCForm = [];
    this.WordShape = "";
    this.Syllables = 1;
    this.Reverse = [];
    this.html = "";
    this.isSightWord = false;
}

/**
 * Class that holds text fragment information
 * @param {String} str The text of the fragment
 * @param {Boolean} isSpace <code>TRUE</code> if this fragment is inter-sentence space, otherwise <code>FALSE</code>.
 * @returns {TextFragment}
 */
export function TextFragment(str, isSpace) {
    // constructor code
    this.text = str;
    this.isSentence = !isSpace;
    this.isSpace = isSpace;
    this.words = theOneLibSynphony
        .getWordsFromHtmlString(
            jQuery(
                "<div>" +
                    str.replace(/<br><\/br>|<br>|<br \/>|<br\/>/gi, "\n") +
                    "</div>"
            ).text()
        )
        .filter(function(word) {
            return word != "";
        });

    this.wordCount = function() {
        return this.words.length;
    };
}

function WordCache() {
    this.desiredGPCs;
    this.knownGPCs;
    this.selectedWords;
}

export function addBloomSynphonyExtensions() {
    LibSynphony.prototype.setExtraSentencePunctuation = function(extra) {
        // Replace characters that are magic in regexp. As a special case, period is simply removed, since it's already
        // sentence-terminating. If they want to use backslash, they will just have to double it; we can't fix it
        // because we want to allow \u0020 etc. I don't know why <> need to be replaced but they don't work otherwise.
        var extraRe = extra
            .replace(/\\U/g, "\\u") // replace all
            .replace("^", "\\u005E")
            .replace("$", "\\u0024")
            .replace(".", "")
            .replace("*", "\\u002A")
            .replace("~", "\\u007E")
            .replace("[", "\\u005B")
            .replace("]", "\\u005D")
            .replace("<", "\\u003C")
            .replace(">", "\\u003E");
        LibSynphony.prototype.extraSentencePunct = extraRe;
        // Although the method name says "add", it actually resets the meaning of "SEP" in regular expressions to whatever we give it here.
        // The literal string is taken from bloom-xregexp_categories.js, copied because I can't figure out how a literal string there
        // can be accessed here.
        XRegExp.addUnicodeData([
            {
                name: "SEP",
                alias: "Sentence_Ending_Punctuation",
                bmp:
                    extraRe +
                    "\u17D4" + // requested for Khmer (BL-12757)
                    "\u0021\u002e\u003f\u055c\u055e\u0589\u061f\u06d4\u0700\u0701\u0702\u0964\u0965\u104b\u1362\u1367\u1368\u166e\u1803\u1809\u1944\u1945\u203c\u203d\u2047\u2048\u2049\u3002\ufe52\ufe56\ufe57\uff01\uff0e\uff1f\uff61\u00a7"
            }
        ]);
    };

    /**
     * Takes an HTML string and returns an array of fragments containing sentences and inter-sentence spaces
     * @param {String} textHTML The HTML text to split
     * @returns {Array} An array of <code>TextFragment</code> objects
     */
    LibSynphony.prototype.stringToSentences = function(textHTML) {
        // place holders
        const delimiter = String.fromCharCode(0);
        // Lets us treat various forms of <br> as a single whitespace character in regexps
        const htmlLineBreakReplacement = String.fromCharCode(1);
        // Lets us treat CRLF as single whitespace character in regexps.
        const windowsLineBreakReplacement = String.fromCharCode(2);
        // Inserted at the start of the text of non-sentence fragments to mark them as such until we actually make a Fragment object.
        const nonSentenceMarker = String.fromCharCode(3);
        // Lets us treat an opening tag as  single character in regexps. A matching list of the actual tags in openTags is used to restore them.
        const openingTagReplacement = String.fromCharCode(4); // u0004 is a replacement character for all other opening html tags
        // Lets us treat a closing tag as  single character in regexps. A matching list of the actual tags in closeTags is used to restore them.
        const closingTagReplacement = String.fromCharCode(5); // u0005 is a replacement character for all other closing html tags
        // Lets us treat a self-closing tag as a single character in regexps. A matching list of the actual tags in selfTags is used to restore them.
        const selfClosingTagReplacement = String.fromCharCode(6); // u0006 is a replacement character for all other self-closing html tags
        // This replaces an empty HTML element, which by the time we use it is openingTagReplacement immediately followed by closingTagReplacement.
        // We convert it back to that sequence before restoring them.
        const emptyTagReplacement = String.fromCharCode(7);
        // Lets us use a single character in regexps in place of "&nbsp;"
        const nbspReplacement = String.fromCharCode(8);
        if (textHTML === null) textHTML = "";
        // look for html break tags, replace them with the htmlLineBreak place holder
        let regex = /(<br><\/br>|<br>|<br \/>|<br\/>)/g;
        textHTML = textHTML.replace(regex, htmlLineBreakReplacement);

        // look for Windows line breaks, replace them with the windowsLineBreak place holder
        regex = /(\r\n)/g;
        textHTML = textHTML.replace(regex, windowsLineBreakReplacement);

        // collect opening html tags and replace with tagHolderOpen place holder
        // Remember, tag names can be as short as a single character!  See BL-10119.
        // We want to match tags like <strong> or <a href="https://sil.org/"> but not
        // <br /> or <img src="image.png"/>.  Self-closing tags are matched later.
        const regexOpenTag = /<[a-zA-Z]+([^<>]*[^\/<>])?>/g;
        const openTags = textHTML.match(regexOpenTag);
        textHTML = textHTML.replace(regexOpenTag, openingTagReplacement);

        // collect closing html tags and replace with tagHolderClose place holder
        // We want to match tags like </span> or </u>.
        const regexCloseTag = /<[\/][a-zA-Z]+>/g;
        const closeTags = textHTML.match(regexCloseTag);
        textHTML = textHTML.replace(regexCloseTag, closingTagReplacement);

        // collect self-closing html tags and replace with tagHolderSelf place holder
        // We want to match tags like <br/> or <img src="picture.jpg" />.
        const regexSelfTag = /<[a-zA-Z]+[^<>]*[\/]>/g;
        const selfTags = textHTML.match(regexSelfTag);
        textHTML = textHTML.replace(regexSelfTag, selfClosingTagReplacement);

        // collect empty html tags and replace with tagHolderEmpty place holder
        const regexEmptyTag = new RegExp(
            openingTagReplacement + closingTagReplacement,
            "g"
        );
        const emptyTags = textHTML.match(regexEmptyTag);
        textHTML = textHTML.replace(regexEmptyTag, emptyTagReplacement);

        // replace &nbsp; with nbsp place holder
        textHTML = textHTML.replace(/&nbsp;/g, nbspReplacement);

        // look for paragraph ending sequences
        regex = XRegExp(
            "[^\\p{PEP}]*[\\p{PEP}]+" + "|[^\\p{PEP}]+$", // break on all paragraph ending punctuation (PEP)
            "g"
        );

        // break the text into paragraphs
        var paragraphs = XRegExp.match(textHTML, regex);

        const zwsp = "\u200B"; // zero-width-space"

        // We require at least one space between sentences, unless things have been configured so that
        // space IS a sentence-ending punctuation. In that case, zero or more.
        // zero-width space does not COUNT as a space (if it's the only thing between period and next upper-case letter),
        // but they may be interspersed.
        // Review: Do selfClosingTagReplacement and emptyTagReplacement belong here or in
        // sentenceSpacePadChars? We have specific unit tests that fail if they are put there:
        // one asserting that an image counts as white space between sentences, and one that
        // an empty span counts. The former strikes me as unlikely (an image is probably not
        // white space) and the latter very strange: an empty span typically isn't visible at
        // all. I'm leaving it this way since we're close to shipping and there may be a good
        // reason I don't know, and a change is not needed to fix the problem.
        const sentenceSpaceChars =
            "\\s\\p{PEP}" +
            selfClosingTagReplacement +
            emptyTagReplacement +
            nbspReplacement;

        // Characters that may be considered part of the white space between sentences, but
        // do not themselves constitute a gap that defines a sentence.
        const sentenceSpacePadChars = zwsp;

        // any number (including zero) of characters we consider white, including the ones that
        // may be included in whitespace but don't COUNT as sentence-breaking white space
        const whiteSpacePattern =
            "[" + sentenceSpaceChars + sentenceSpacePadChars + "]*";

        const intersentenceSpace =
            "(" +
            whiteSpacePattern +
            (LibSynphony.prototype.extraSentencePunct &&
            LibSynphony.prototype.extraSentencePunct.indexOf("\\u0020") >= 0
                ? "" // nothing more needed
                : // One definite (not zwsp etc.) white character, possibly followed by arbitrarily more space
                  "[" + sentenceSpaceChars + "]" + whiteSpacePattern) +
            ")";
        // Note that categories Pf and Pi can both act as either Ps or Pe
        // (See https://issues.bloomlibrary.org/youtrack/issue/BL-5063.)
        // characters that can follow the SEP: single or double quotes,
        // Pe: close punctuation (closing brackets),
        // Pf: final punctuation (closing quotes)
        // Pi: Initial punctuation (opening quotes)
        var trailingPunct =
            "['\"\\p{Pe}\\p{Pf}\\p{Pi}" + closingTagReplacement + "]";
        // What can follow the separator and be considered part of the preceding sentence.
        // These kinds of spaces are allowed, but only before a trailingPunct; otherwise,
        // they are considered part of the inter-sentence white space.
        // \\u202F: narrow non-breaking space that our long-press inserts for non-breaking space.
        // Using a non-capturing group here, because it's only to allow the space+trailing
        // sequence to repeat; we don't want to use it separately in the result.
        var afterSEP =
            "(?:[" + nbspReplacement + "\\u202F]*" + trailingPunct + ")*";

        // regex to find sentence ending sequences and inter-sentence space
        // \p{SEP} is defined as a list of sentence ending punctuation characters by a call to XRegExp.addUnicodeData
        // in LibSynphony.prototype.setExtraSentencePunctuation (or perhaps elsewhere in tests?)
        regex = XRegExp(
            "([\\p{SEP}]+" + // sentence ending punctuation (SEP)
            afterSEP + // what can follow SEP in same sentence,
            ")" + // then end group for the sentence
            "([" +
            openingTagReplacement +
            "]*)" +
            intersentenceSpace +
            "([" +
            closingTagReplacement +
            "]*)" +
            "(?![^\\p{L}]*" + // may be followed by non-letter chars
                "[\\p{Ll}\\p{SCP}]+)", // first letter following is not lower case. (This works by consuming all the lowercase letters/etc. up until the first uppercase letter/etc)
            "g"
        );

        var returnVal = new Array();
        for (var i = 0; i < paragraphs.length; i++) {
            // mark boundaries between sentences and inter-sentence space
            var paragraph = XRegExp.replace(
                paragraphs[i],
                regex,
                "$1" +
                    delimiter +
                    nonSentenceMarker +
                    "$2" +
                    "$3" +
                    "$4" +
                    delimiter
            );

            // Phrase Delimiting
            // It's possible to use a fancy regex-based approach too (akin to sentence splitting regex)
            // But that's not so intuitive nor easy to obtain/verify correctness
            // due to many corner cases... need to consider sentence continuing punctuation, etc
            // Instead, let's start with a simple, easy to understand approach:
            //    "|" character is a phrase delimiter. Context doesn't matter.
            //    (But collapse empty entries created from having multiple "|" character in a row)
            // It should be easier for us to implement, test, and communicate, and easier for the user to understand.
            paragraph = XRegExp.replace(paragraph, /(\|+)/g, "$1" + delimiter);

            // restore line breaks
            paragraph = paragraph.replace(
                new RegExp(htmlLineBreakReplacement, "g"),
                "<br />"
            );
            paragraph = paragraph.replace(
                new RegExp(windowsLineBreakReplacement, "g"),
                "\r\n"
            );

            var unclosedTags = [];
            // split the paragraph into sentences and
            var fragments = paragraph.split(delimiter);
            var prevFragmentWithoutMarkup = "";
            for (var j = 0; j < fragments.length; j++) {
                var fragment = fragments[j];

                const fragmentWithoutMarkup = fragment.replace(
                    new RegExp(
                        "[" +
                            openingTagReplacement +
                            closingTagReplacement +
                            selfClosingTagReplacement +
                            emptyTagReplacement +
                            "]",
                        "g"
                    ),
                    ""
                );

                // if some tags from earlier segments are still open, reopen at start
                // For example, if earlier segments opened <span...> and then <em> and closed neither,
                // we want to re-open them in that order. They remain in the unclosedTags list.
                // That is, we will insert <span...><em> at the start of the fragment.
                // This should come after the special character that identifies white-space
                // segments, if it is present.
                if (fragment.startsWith(nonSentenceMarker)) {
                    fragment =
                        nonSentenceMarker +
                        unclosedTags.join("") +
                        fragment.substring(1);
                } else {
                    fragment = unclosedTags.join("") + fragment;
                }

                // put the empty html tags back in
                while (fragment.indexOf(emptyTagReplacement) > -1)
                    fragment = fragment.replace(
                        new RegExp(emptyTagReplacement),
                        emptyTags.shift()
                    );

                // put the opening html tags back in
                while (fragment.indexOf(openingTagReplacement) > -1) {
                    const tag = openTags.shift();
                    fragment = fragment.replace(
                        new RegExp(openingTagReplacement),
                        tag
                    );
                    unclosedTags.push(tag);
                }

                // put the closing html tags back in
                // (unless this is an empty segment...then just leave them out; already closed at end of previous segment)
                while (fragment.indexOf(closingTagReplacement) > -1) {
                    const closeTag = closeTags.shift();
                    fragment = fragment.replace(
                        new RegExp(closingTagReplacement),
                        closeTag
                    );
                    unclosedTags.pop();
                }

                // If some tags are still unclosed, close them. (Will reopen next fragment).
                // For example, if <span...> was opened, and later <em>, and neither has been
                // closed, unclosedTags contains <span...>, <em>.
                // We want to append </em></span>

                for (let i = unclosedTags.length - 1; i >= 0; i--) {
                    const tagToClose = unclosedTags[i];
                    // convert from opening tag to corresponding closing tag.
                    // (Drop the opening wedge with substring, prepend '</'.
                    // Then if there is anything after the tag, remove it.
                    // If the regex doesn't match, that makes it something like <em>,
                    // and there's nothing we need to remove; replace just does nothing.)
                    const closingTag =
                        "</" + tagToClose.substring(1).replace(/ .*>/, ">");
                    fragment = fragment + closingTag;
                }

                // put the self-closing html tags back in
                while (fragment.indexOf(selfClosingTagReplacement) > -1)
                    fragment = fragment.replace(
                        new RegExp(selfClosingTagReplacement),
                        selfTags.shift()
                    );

                // put nbsp back in
                fragment = fragment.replace(
                    new RegExp(nbspReplacement, "g"),
                    "&nbsp;"
                );

                // check to avoid empty segments at the end
                if (
                    j < fragments.length - 1 ||
                    fragmentWithoutMarkup.length > 0
                ) {
                    // is this space between sentences?
                    if (fragment.substring(0, 1) === nonSentenceMarker) {
                        returnVal.push(
                            new TextFragment(fragment.substring(1), true)
                        );
                    } else if (
                        j > 0 &&
                        prevFragmentWithoutMarkup.endsWith("|") &&
                        fragmentWithoutMarkup.match(/^\s/) // current fragment starts with whitespace
                    ) {
                        // Check for a phrase marker at the end of the previous fragment followed by
                        // whitespace at the beginning of this fragment to maintain "interphrase" spacing.
                        // Users can place the phrase marker (|) before or after inter-word spacing (or
                        // even in the middle of a word I suppose).  The regex for splitting on a phrase
                        // marker is intentionally kept simple, but we need to make up for its simplicity
                        // here with this special check and fix.
                        // See https://issues.bloomlibrary.org/youtrack/issue/BL-10569.
                        const leadingSpaceCount = fragment.search(/\S|$/); // get location of first non-whitespace character or end of string
                        // Add the leading whitespace as a space TextFragment
                        returnVal.push(
                            new TextFragment(
                                fragment.substring(0, leadingSpaceCount),
                                true
                            )
                        );
                        // If there's anything left over, add the rest of the fragment as a regular sentence TextFragment
                        if (leadingSpaceCount < fragment.length)
                            returnVal.push(
                                new TextFragment(
                                    fragment.substring(leadingSpaceCount),
                                    false
                                )
                            );
                    } else {
                        returnVal.push(new TextFragment(fragment, false));
                    }
                }
                prevFragmentWithoutMarkup = fragmentWithoutMarkup;
            }
        }

        return returnVal;
    };

    /**
     * Reads the file passed in the fileInputElement and calls the callback function when finished
     * @param {Element} fileInputElement
     * @param {Function} callback Function with one parameter, which will be TRUE if successful.
     */
    LibSynphony.prototype.loadLanguageData = function(
        fileInputElement,
        callback
    ) {
        var file = fileInputElement.files[0];

        if (!file) return;

        var reader = new FileReader();
        reader.onload = function(e) {
            callback(theOneLibSynphony.langDataFromString(e.target.result));
        };
        reader.readAsText(file);
    };

    /**
     * Returns just the Name property (the actual word) of the selected DataWord objects.
     * @param {Array} aDesiredGPCs The list of graphemes targeted by this search
     * @param {Array} aKnownGPCs The list of graphemes known by the reader
     * @param {Boolean} restrictToKnownGPCs If <code>TRUE</code> then words will only contain graphemes in the <code>aKnownGPCs</code> list. If <code>FALSE</code> then words will contain at least one grapheme from the <code>aDesiredGPCs</code> list.
     * @param {Boolean} allowUpperCase
     * @param {Array} aSyllableLengths
     * @param {Array} aSelectedGroups
     * @param {Array} aPartsOfSpeech
     * @returns {Array} An array of strings
     */
    LibSynphony.prototype.selectGPCWordNamesWithArrayCompare = function(
        aDesiredGPCs,
        aKnownGPCs,
        restrictToKnownGPCs,
        allowUpperCase,
        aSyllableLengths,
        aSelectedGroups,
        aPartsOfSpeech
    ) {
        var gpcs = theOneLibSynphony.selectGPCWordsWithArrayCompare(
            aDesiredGPCs,
            aKnownGPCs,
            restrictToKnownGPCs,
            allowUpperCase,
            aSyllableLengths,
            aSelectedGroups,
            aPartsOfSpeech
        );
        return _.pluck(gpcs, "Name");
    };

    /**
     * Returns a list of words that meet the requested criteria.
     * @param {Array} aDesiredGPCs The list of graphemes targeted by this search
     * @param {Array} aKnownGPCs The list of graphemes known by the reader
     * @param {Boolean} restrictToKnownGPCs If <code>TRUE</code> then words will only contain graphemes in the <code>aKnownGPCs</code> list. If <code>FALSE</code> then words will contain at least one grapheme from the <code>aDesiredGPCs</code> list.
     * @param {Boolean} allowUpperCase
     * @param {Array} aSyllableLengths
     * @param {Array} aSelectedGroups
     * @param {Array} aPartsOfSpeech
     * @returns {Array} An array of WordObject objects
     */
    LibSynphony.prototype.selectGPCWordsFromCache = function(
        aDesiredGPCs,
        aKnownGPCs,
        restrictToKnownGPCs,
        allowUpperCase,
        aSyllableLengths,
        aSelectedGroups,
        aPartsOfSpeech
    ) {
        // check if the list of graphemes changed
        if (!theOneWordCache) {
            theOneWordCache = new WordCache();
        } else {
            if (
                theOneWordCache.desiredGPCs.length !== aDesiredGPCs.length ||
                theOneWordCache.knownGPCs.length !== aKnownGPCs.length ||
                _.intersection(theOneWordCache.desiredGPCs, aDesiredGPCs)
                    .length !== aDesiredGPCs.length ||
                _.intersection(theOneWordCache.knownGPCs, aKnownGPCs).length !==
                    aKnownGPCs.length
            ) {
                theOneWordCache = new WordCache();
            } else {
                // return the cached list
                return theOneWordCache.selectedWords;
            }
        }

        theOneWordCache.desiredGPCs = aDesiredGPCs;
        theOneWordCache.knownGPCs = aKnownGPCs;
        theOneWordCache.selectedWords = theOneLibSynphony.selectGPCWordsWithArrayCompare(
            aDesiredGPCs,
            aKnownGPCs,
            restrictToKnownGPCs,
            allowUpperCase,
            aSyllableLengths,
            aSelectedGroups,
            aPartsOfSpeech
        );

        return theOneWordCache.selectedWords;
    };

    /**
     * Adds a new DataGPC object to Vocabulary Group
     * @param grapheme Either a single grapheme (string) or an array of graphemes
     */
    LanguageData.prototype.addGrapheme = function(grapheme) {
        if (!Array.isArray(grapheme)) grapheme = [grapheme];

        for (var i = 0; i < grapheme.length; i++) {
            var g = grapheme[i].toLowerCase();

            // check if the grapheme already exists
            var exists = _.any(this.GPCS, function(item) {
                return item.GPC === g;
            });

            if (!exists) this.GPCS.push(new DataGPC(g));
        }
    };

    /**
     * Adds a new DataWord object to Vocabulary Group
     * @param word Either a single word (string) or an array of words
     * @param {int} [freq] The number of times this word occurs
     */
    LanguageData.prototype.addWord = function(word, freq) {
        var sortedGraphemes = _.sortBy(
            _.pluck(this.GPCS, "Grapheme"),
            "length"
        ).reverse();

        // if this is a single word...
        if (!Array.isArray(word)) word = [word];

        for (var i = 0; i < word.length; i++) {
            var w = word[i].toLowerCase();

            // check if the word already exists
            var dw = this.findWord(w);
            if (!dw) {
                dw = new DataWord(w);
                dw.GPCForm = this.getGpcForm(w, sortedGraphemes);
                if (freq) dw.Count = freq;
                this.group1.push(dw);
            } else {
                if (freq) dw.Count += freq;
                else dw.Count += 1;
            }
        }
    };

    /**
     * Searches existing list for a word
     * @param {String} word
     * @returns {DataWord} Returns undefined if not found.
     */
    LanguageData.prototype.findWord = function(word) {
        for (var i = 1; i <= this.VocabularyGroups; i++) {
            var found = _.find(this["group" + i], function(item) {
                return item.Name === word;
            });

            if (found) return found;
        }

        return null;
    };

    /**
     * Gets the GPCForm value of a word
     * @param {String} word
     * @param {Array} sortedGraphemes Array of all graphemes, sorted by descending length
     * @returns {Array}
     */
    LanguageData.prototype.getGpcForm = function(word, sortedGraphemes) {
        var gpcForm = [];
        var hit = 1; // used to prevent infinite loop if word contains letters not in the list

        while (word.length > 0) {
            hit = 0;

            for (var i = 0; i < sortedGraphemes.length; i++) {
                var g = sortedGraphemes[i];

                if (word.substr(g.length * -1) === g) {
                    gpcForm.unshift(g);
                    word = word.substr(0, word.length - g.length);
                    hit = 1;
                    break;
                }
            }

            // If hit = 0, the last character still in the word is not in the list of graphemes.
            // We are adding it now  so the gpcForm returned will be as accurate as possible, and
            // will contain all the characters in the word.
            if (hit === 0) {
                var lastChar = word.substr(-1);
                var lastCharCode = lastChar.charCodeAt(0);
                // JS isn't very good at handling Unicode values beyond \xFFFF
                // We don't want to split up surrogate pairs which JS counts as two separate characters
                if (this.isPartOfSurrogatePair(lastCharCode)) {
                    gpcForm.unshift(word.substr(-2));
                    word = word.substr(0, word.length - 2);
                } else {
                    gpcForm.unshift(lastChar);
                    word = word.substr(0, word.length - 1);
                }
            }
        }

        return gpcForm;
    };

    LanguageData.prototype.isPartOfSurrogatePair = function(charCode) {
        return 0xd800 <= charCode && charCode <= 0xdfff;
    };

    /**
     * Because of the way SynPhony attaches properties to the group array, the standard JSON.stringify() function
     * does not return a correct representation of the LanguageData object.
     * @param {String} langData
     * @returns {String}
     */
    LanguageData.toJSON = function(langData) {
        // get the group arrays
        var n = langData["VocabularyGroups"];

        for (var a = 1; a < n + 1; a++) {
            var group = langData["group" + a];
            var index = "groupIndex" + a;
            langData[index] = {};

            // for each group, get the properties (contain '__')
            var keys = _.filter(_.keys(group), function(k) {
                return k.indexOf("__") > -1;
            });
            for (var i = 0; i < keys.length; i++) {
                langData[index][keys[i]] = group[keys[i]];
            }
        }

        return JSON.stringify(langData);
    };

    LanguageData.fromJSON = function(jsonString) {
        var langData = JSON.parse(jsonString);

        // convert the groupIndex objects back to properties of the group array
        var n = langData["VocabularyGroups"];

        for (var a = 1; a < n + 1; a++) {
            var group = langData["group" + a];
            var index = "groupIndex" + a;
            var keys = _.keys(langData[index]);

            for (var i = 0; i < keys.length; i++) {
                group[keys[i]] = langData[index][keys[i]];
                langData[index][keys[i]] = null;
            }
        }

        return jQuery.extend(true, new LanguageData(), langData);
    };
}

// normally this just happens...immediately executed...but if that doesn't work for some
// mysterious reason (see spreadsheetBundleRoot.ts) we can call it explicitly.
addBloomSynphonyExtensions();
